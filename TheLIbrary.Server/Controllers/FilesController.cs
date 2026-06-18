using System.IO.Compression;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SharpCompress.Archives;
using SharpCompress.Readers;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FilesController : ControllerBase
{
    private readonly LibraryDbContext _db;
    private readonly CalibreConverter _converter;

    public FilesController(LibraryDbContext db, CalibreConverter converter)
    {
        _db = db;
        _converter = converter;
    }

    // Enabled library roots PLUS the dedupe archive folder when it is a full path
    // outside the library — so archived files (on the Archived Files page) can be
    // previewed without tripping the "outside library locations" guard.
    private async Task<List<string>> GetPreviewRootsAsync(CancellationToken ct)
    {
        var roots = await _db.LibraryLocations.AsNoTracking()
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);
        var archive = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.DedupeArchiveFolder)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(archive))
        {
            var a = archive.Trim().Replace('\\', '/').TrimEnd('/');
            if (a.Contains('/')) roots.Add(a); // absolute archive path → allow it too
        }
        return roots;
    }

    // Streams a LocalBookFile's actual bytes for in-browser preview. Supported
    // formats: epub (via epub.js client-side), pdf (native browser viewer),
    // txt (plain text). MOBI / AZW / LIT etc are rejected with 415 — those
    // need server-side conversion via `ebook-convert`, which is a follow-up.
    //
    // Security: the resolved disk path must live inside one of the enabled
    // LibraryLocation roots. Without this guard a tampered LocalBookFile.FullPath
    // could expose any file on disk to anyone who can reach the API.
    [HttpGet("{fileId:int}/preview")]
    public async Task<IActionResult> Preview(
        int fileId, [FromQuery] string format, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(format))
            return BadRequest(new { error = "format query parameter is required" });

        var lbf = await _db.LocalBookFiles.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (lbf is null) return NotFound(new { error = "File record not found" });

        // Resolve the actual source file. lbf.FullPath may be a directory (library
        // layout) or a direct file path (flat layout). For conversion we need a file.
        var supportedConversions = new[] { "mobi", "azw", "azw3", "fb2", "lit", "docx", "odt" };
        var fmt = format.ToLowerInvariant();

        string? conversionSource = null;
        if (fmt == "epub")
        {
            // Client always requests format=epub for convertible types.
            // Check if storedPath itself is a convertible file, or find one inside the directory.
            var storedExt = Path.GetExtension(lbf.FullPath).TrimStart('.').ToLowerInvariant();
            if (supportedConversions.Contains(storedExt) && System.IO.File.Exists(lbf.FullPath))
            {
                conversionSource = lbf.FullPath;
            }
            else if (System.IO.Directory.Exists(lbf.FullPath))
            {
                try
                {
                    conversionSource = System.IO.Directory.EnumerateFiles(lbf.FullPath)
                        .FirstOrDefault(f => supportedConversions.Contains(
                            Path.GetExtension(f).TrimStart('.').ToLowerInvariant()));
                }
                catch { /* ignore enumeration errors; conversionSource stays null */ }
            }
        }
        else if (supportedConversions.Contains(fmt))
        {
            // Direct request for a convertible extension (e.g. format=mobi).
            var storedExt = Path.GetExtension(lbf.FullPath).TrimStart('.').ToLowerInvariant();
            if (storedExt == fmt && System.IO.File.Exists(lbf.FullPath))
            {
                conversionSource = lbf.FullPath;
            }
            else if (System.IO.Directory.Exists(lbf.FullPath))
            {
                try
                {
                    conversionSource = System.IO.Directory.EnumerateFiles(lbf.FullPath)
                        .FirstOrDefault(f => string.Equals(
                            Path.GetExtension(f).TrimStart('.'), fmt,
                            StringComparison.OrdinalIgnoreCase));
                }
                catch { /* ignore */ }
            }
        }

        if (conversionSource is not null)
        {
            // Containment guard MUST run before we read/convert/serve the file —
            // a tampered LocalBookFile.FullPath could otherwise point the converter
            // at any file on disk. The non-conversion path below enforces this via
            // FilePreviewResolver; the conversion path has to do it explicitly.
            var convertRoots = await GetPreviewRootsAsync(ct);
            if (!FilePreviewResolver.IsInsideAnyRoot(conversionSource, convertRoots))
                return StatusCode(403, new { error = "Refusing to serve a file outside enabled library locations" });

            try
            {
                var convertedPath = await _converter.ConvertToEpubAsync(conversionSource, ct);
                if (System.IO.File.Exists(convertedPath))
                {
                    var bytes = await System.IO.File.ReadAllBytesAsync(convertedPath, ct);
                    try { System.IO.File.Delete(convertedPath); } catch { /* best effort */ }
                    Response.Headers["Content-Disposition"] = $"inline; filename=\"{Path.GetFileNameWithoutExtension(conversionSource)}.epub\"";
                    return File(bytes, "application/epub+zip");
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = $"On-the-fly conversion to EPUB failed: {ex.Message}" });
            }
        }

        var roots = await GetPreviewRootsAsync(ct);

        var resolution = FilePreviewResolver.Resolve(lbf.FullPath, format, roots);
        if (resolution.Ok is null)
        {
            return resolution.Failure switch
            {
                FilePreviewResolver.FailureKind.UnsupportedFormat
                    => StatusCode(415, new { error = $"Preview not supported for '.{format}'. Supported: epub, pdf, txt, rtf, cbz, zip." }),
                FilePreviewResolver.FailureKind.OutsideLibrary
                    => StatusCode(403, new { error = "Refusing to serve a file outside enabled library locations" }),
                _ => NotFound(new { error = $"No '.{format}' file found at this record" }),
            };
        }

        if (!System.IO.File.Exists(resolution.Ok.FullPath))
            return NotFound(new { error = "File no longer exists on disk" });

        // RTF has no native browser viewer, so convert it to plain text on the
        // fly (RtfPipe) and serve that for the txt pane instead of the raw
        // markup. Read fully — RTF files are small enough not to stream.
        if (fmt == "rtf")
        {
            try
            {
                var rtf = await System.IO.File.ReadAllTextAsync(resolution.Ok.FullPath, ct);
                return Content(RtfTextExtractor.ExtractText(rtf), "text/plain; charset=utf-8");
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = $"Could not read RTF: {ex.Message}" });
            }
        }

        // Explicitly set `inline` disposition so the browser's PDF viewer
        // renders the file in an <iframe> instead of triggering a download.
        // PhysicalFile() with a fileName argument sets `attachment` by default,
        // which is why an earlier version of this endpoint kept downloading
        // the PDF rather than previewing it. We still expose the filename so
        // the browser's "save as" dialog picks a sensible default.
        var safeName = resolution.Ok.FileName.Replace("\"", "");
        Response.Headers["Content-Disposition"] = $"inline; filename=\"{safeName}\"";

        // EnableRangeProcessing lets the browser stream large EPUB/PDFs without
        // pulling the whole file into memory — epub.js + the native PDF viewer
        // both make byte-range requests.
        return PhysicalFile(
            resolution.Ok.FullPath,
            resolution.Ok.ContentType,
            enableRangeProcessing: true);
    }

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif"
    };

    private async Task<(string? Path, IActionResult? Error)> ResolveFileForDirectAccessAsync(int fileId, CancellationToken ct)
    {
        var lbf = await _db.LocalBookFiles.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (lbf is null) return (null, NotFound(new { error = "File record not found" }));

        // Check format
        var ext = Path.GetExtension(lbf.FullPath).ToLowerInvariant();
        if (ext != ".cbz" && ext != ".zip" && ext != ".cbr" && ext != ".rar")
        {
            // Classic library layout: FullPath is a folder — pick a comic archive
            // inside it (zip/cbz or rar/cbr).
            if (Directory.Exists(lbf.FullPath))
            {
                var fallback = Directory.EnumerateFiles(lbf.FullPath)
                    .FirstOrDefault(f => Path.GetExtension(f) is { } e &&
                        (e.Equals(".cbz", StringComparison.OrdinalIgnoreCase) ||
                         e.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                         e.Equals(".cbr", StringComparison.OrdinalIgnoreCase) ||
                         e.Equals(".rar", StringComparison.OrdinalIgnoreCase)));
                if (fallback != null)
                {
                    lbf.FullPath = fallback;
                }
            }
        }

        var roots = await GetPreviewRootsAsync(ct);

        var canonical = Path.GetFullPath(lbf.FullPath);
        var belongs = roots.Any(r => canonical.StartsWith(Path.GetFullPath(r), StringComparison.OrdinalIgnoreCase));
        if (!belongs)
            return (null, StatusCode(403, new { error = "Refusing to serving a file outside enabled library locations" }));

        if (!System.IO.File.Exists(canonical))
            return (null, NotFound(new { error = "File no longer exists on disk" }));

        return (canonical, null);
    }

    // Image entries (in display order) from a comic archive — SharpCompress
    // reads both zip/cbz and rar/cbr.
    private static List<IArchiveEntry> OrderedImageEntries(IArchive archive)
        => archive.Entries
            .Where(e => !e.IsDirectory && e.Key is not null && ImageExtensions.Contains(Path.GetExtension(e.Key)))
            .OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

    [HttpGet("{fileId:int}/cbz-pages")]
    public async Task<IActionResult> GetCbzPages(int fileId, CancellationToken ct)
    {
        var (path, error) = await ResolveFileForDirectAccessAsync(fileId, ct);
        if (error is not null) return error;

        try
        {
            using var archive = ArchiveFactory.OpenArchive(path!, new ReaderOptions());
            var entries = OrderedImageEntries(archive)
                .Select((e, idx) => new { index = idx, name = e.Key, size = e.Size })
                .ToList();

            return Ok(entries);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = $"Failed to read comic archive: {ex.Message}" });
        }
    }

    [HttpGet("{fileId:int}/cbz-page/{index:int}")]
    public async Task<IActionResult> GetCbzPage(int fileId, int index, CancellationToken ct)
    {
        var (path, error) = await ResolveFileForDirectAccessAsync(fileId, ct);
        if (error is not null) return error;

        try
        {
            using var archive = ArchiveFactory.OpenArchive(path!, new ReaderOptions());
            var entries = OrderedImageEntries(archive);

            if (index < 0 || index >= entries.Count)
                return NotFound(new { error = $"Page index {index} is out of bounds. Max index: {entries.Count - 1}" });

            var entry = entries[index];
            var memoryStream = new MemoryStream();
            using (var stream = entry.OpenEntryStream())
                await stream.CopyToAsync(memoryStream, ct);
            memoryStream.Position = 0;

            var ext = Path.GetExtension(entry.Key!).ToLowerInvariant();
            var contentType = ext switch
            {
                ".png" => "image/png",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                _ => "image/jpeg"
            };

            return File(memoryStream, contentType);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = $"Failed to stream page from archive: {ex.Message}" });
        }
    }
}

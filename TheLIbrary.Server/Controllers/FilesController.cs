using System.IO.Compression;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
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

        // Try converting MOBI, AZW, AZW3, FB2, LIT etc. on-the-fly to EPUB.
        var ext = Path.GetExtension(lbf.FullPath).TrimStart('.').ToLowerInvariant();
        var supportedConversions = new[] { "mobi", "azw", "azw3", "fb2", "lit", "docx", "odt", "cbz", "zip" };
        if (supportedConversions.Contains(ext) && (format.Equals("epub", StringComparison.OrdinalIgnoreCase) || format.Equals(ext, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var convertedPath = await _converter.ConvertToEpubAsync(lbf.FullPath, ct);
                if (System.IO.File.Exists(convertedPath))
                {
                    var bytes = await System.IO.File.ReadAllBytesAsync(convertedPath, ct);
                    try { System.IO.File.Delete(convertedPath); } catch { /* best effort */ }
                    Response.Headers["Content-Disposition"] = $"inline; filename=\"{Path.GetFileNameWithoutExtension(lbf.FullPath)}.epub\"";
                    return File(bytes, "application/epub+zip");
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = $"On-the-fly conversion to EPUB failed: {ex.Message}" });
            }
        }

        var roots = await _db.LibraryLocations.AsNoTracking()
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);

        var resolution = FilePreviewResolver.Resolve(lbf.FullPath, format, roots);
        if (resolution.Ok is null)
        {
            return resolution.Failure switch
            {
                FilePreviewResolver.FailureKind.UnsupportedFormat
                    => StatusCode(415, new { error = $"Preview not supported for '.{format}'. Supported: epub, pdf, txt, cbz, zip." }),
                FilePreviewResolver.FailureKind.OutsideLibrary
                    => StatusCode(403, new { error = "Refusing to serve a file outside enabled library locations" }),
                _ => NotFound(new { error = $"No '.{format}' file found at this record" }),
            };
        }

        if (!System.IO.File.Exists(resolution.Ok.FullPath))
            return NotFound(new { error = "File no longer exists on disk" });

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
        if (ext != ".cbz" && ext != ".zip" && ext != ".rar")
        {
            // Try to find a .cbz or .zip in the folder if the FullPath points to a folder (classic Calibre layout)
            if (Directory.Exists(lbf.FullPath))
            {
                var fallback = Directory.EnumerateFiles(lbf.FullPath)
                    .FirstOrDefault(f => Path.GetExtension(f).Equals(".cbz", StringComparison.OrdinalIgnoreCase) ||
                                         Path.GetExtension(f).Equals(".zip", StringComparison.OrdinalIgnoreCase));
                if (fallback != null)
                {
                    lbf.FullPath = fallback;
                }
            }
        }

        var roots = await _db.LibraryLocations.AsNoTracking()
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);

        var canonical = Path.GetFullPath(lbf.FullPath);
        var belongs = roots.Any(r => canonical.StartsWith(Path.GetFullPath(r), StringComparison.OrdinalIgnoreCase));
        if (!belongs)
            return (null, StatusCode(403, new { error = "Refusing to serving a file outside enabled library locations" }));

        if (!System.IO.File.Exists(canonical))
            return (null, NotFound(new { error = "File no longer exists on disk" }));

        return (canonical, null);
    }

    [HttpGet("{fileId:int}/cbz-pages")]
    public async Task<IActionResult> GetCbzPages(int fileId, CancellationToken ct)
    {
        var (path, error) = await ResolveFileForDirectAccessAsync(fileId, ct);
        if (error is not null) return error;

        try
        {
            using var zip = ZipFile.OpenRead(path!);
            var entries = zip.Entries
                .Where(e => ImageExtensions.Contains(Path.GetExtension(e.FullName)))
                .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
                .Select((e, idx) => new { index = idx, name = e.FullName, size = e.Length })
                .ToList();

            return Ok(entries);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = $"Failed to read zip archive: {ex.Message}" });
        }
    }

    [HttpGet("{fileId:int}/cbz-page/{index:int}")]
    public async Task<IActionResult> GetCbzPage(int fileId, int index, CancellationToken ct)
    {
        var (path, error) = await ResolveFileForDirectAccessAsync(fileId, ct);
        if (error is not null) return error;

        try
        {
            using var zip = ZipFile.OpenRead(path!);
            var entries = zip.Entries
                .Where(e => ImageExtensions.Contains(Path.GetExtension(e.FullName)))
                .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (index < 0 || index >= entries.Count)
                return NotFound(new { error = $"Page index {index} is out of bounds. Max index: {entries.Count - 1}" });

            var entry = entries[index];
            using var stream = entry.Open();
            var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream, ct);
            memoryStream.Position = 0;

            var ext = Path.GetExtension(entry.FullName).ToLowerInvariant();
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

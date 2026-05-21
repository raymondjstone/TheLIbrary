using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FilesController : ControllerBase
{
    private readonly LibraryDbContext _db;
    public FilesController(LibraryDbContext db) { _db = db; }

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
                    => StatusCode(415, new { error = $"Preview not supported for '.{format}'. Supported: epub, pdf, txt." }),
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
}

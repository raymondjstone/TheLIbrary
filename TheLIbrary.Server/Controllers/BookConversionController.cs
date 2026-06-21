using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.IO;

namespace TheLibrary.Server.Controllers;

// On-demand format normalisation: convert one of a book's existing copies to EPUB
// via Calibre's ebook-convert, drop the result next to the source, and track it as
// a new local file. Useful for books only held as .lit/.pdb/.pdf/.mobi so a
// reader (reMarkable, OPDS clients) always has a usable EPUB.
[ApiController]
[Route("api/books")]
public sealed class BookConversionController : ControllerBase
{
    private readonly LibraryDbContext _db;
    private readonly IFileSystem _fs;
    private readonly CalibreConverter _converter;

    public BookConversionController(LibraryDbContext db, IFileSystem fs, CalibreConverter converter)
    {
        _db = db; _fs = fs; _converter = converter;
    }

    public sealed record ConvertResult(bool Converted, string? Path, string? Message);

    // Readability order for choosing which source copy to convert from.
    private static readonly string[] SourcePreference =
        { "azw3", "azw", "mobi", "fb2", "pdb", "lit", "html", "htmlz", "rtf", "doc", "docx", "txt", "pdf", "cbz", "cbr" };

    [HttpPost("{id:int}/convert-to-epub")]
    public async Task<ActionResult<ConvertResult>> ConvertToEpub(int id, CancellationToken ct)
    {
        if (!_converter.IsConfigured)
            return BadRequest(new { error = "Calibre ebook-convert is not configured (Calibre:EbookConvert)." });

        var book = await _db.Books.Include(b => b.LocalFiles).FirstOrDefaultAsync(b => b.Id == id, ct);
        if (book is null) return NotFound(new { error = "Book not found." });

        // Already has a usable EPUB on disk → nothing to do.
        if (book.LocalFiles.Any(f => IsEpub(f.FullPath) && _fs.FileExists(f.FullPath)))
            return Ok(new ConvertResult(false, null, "Book already has an EPUB copy."));

        // Pick the best convertible source that exists on disk (healthy preferred).
        var source = book.LocalFiles
            .Where(f => _fs.FileExists(f.FullPath) && IsConvertibleSource(f.FullPath))
            .OrderBy(f => f.IntegrityOk == false ? 1 : 0)
            .ThenBy(f => SourceRank(f.FullPath))
            .ThenBy(f => f.Id)
            .FirstOrDefault();
        if (source is null)
            return BadRequest(new { error = "No convertible source file found for this book." });

        string tempEpub;
        try
        {
            tempEpub = await _converter.ConvertToEpubAsync(source.FullPath, ct);
        }
        catch (CalibreConversionException ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }

        // Place the EPUB next to the source (forward-slash paths; the library lives
        // on the Linux mount), with a collision-safe name.
        var srcNorm = source.FullPath.Replace('\\', '/');
        var dir = srcNorm[..srcNorm.LastIndexOf('/')];
        var stem = Path.GetFileNameWithoutExtension(srcNorm);
        var dest = await UniqueAsync($"{dir}/{stem}.epub", ct);

        await _fs.MoveFileAsync(tempEpub, dest, overwrite: false, ct);

        var row = new LocalBookFile
        {
            BookId = book.Id,
            AuthorId = source.AuthorId,
            AuthorFolder = source.AuthorFolder,
            TitleFolder = source.TitleFolder,
            FullPath = dest,
            NormalizedTitle = source.NormalizedTitle,
            ModifiedAt = DateTime.UtcNow,
        };
        _db.LocalBookFiles.Add(row);
        await _db.SaveChangesAsync(ct);

        return Ok(new ConvertResult(true, dest, $"Converted {Path.GetFileName(source.FullPath)} → EPUB."));
    }

    private static bool IsEpub(string path)
        => Path.GetExtension(path).Equals(".epub", StringComparison.OrdinalIgnoreCase);

    private static bool IsConvertibleSource(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".epub", StringComparison.OrdinalIgnoreCase)) return false;
        return CalibreScanner.EbookExtensions.Contains(ext);
    }

    private static int SourceRank(string path)
    {
        var e = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var idx = Array.IndexOf(SourcePreference, e);
        return idx < 0 ? int.MaxValue : idx;
    }

    private async Task<string> UniqueAsync(string desired, CancellationToken ct)
    {
        if (!await _fs.FileExistsAsync(desired, ct)) return desired;
        var dir = desired[..desired.LastIndexOf('/')];
        var stem = Path.GetFileNameWithoutExtension(desired);
        for (var i = 2; i < 1000; i++)
        {
            var next = $"{dir}/{stem}_{i}.epub";
            if (!await _fs.FileExistsAsync(next, ct)) return next;
        }
        return $"{dir}/{stem}_{DateTime.UtcNow:yyyyMMddHHmmss}.epub";
    }
}

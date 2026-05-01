using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;

namespace TheLibrary.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BooksController : ControllerBase
{
    private readonly LibraryDbContext _db;
    public BooksController(LibraryDbContext db) { _db = db; }

    public sealed record OwnershipRequest(bool Owned);

    // Manual ownership override. Independent of any scanned local files —
    // a book is considered owned if it has local files OR this flag is set.
    [HttpPost("{id:int}/ownership")]
    public async Task<IActionResult> SetOwnership(int id, [FromBody] OwnershipRequest body, CancellationToken ct)
    {
        var book = await _db.Books.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (book is null) return NotFound();

        book.ManuallyOwned = body.Owned;
        book.ManuallyOwnedAt = body.Owned ? DateTime.UtcNow : null;
        await _db.SaveChangesAsync(ct);

        var hasLocalFiles = await _db.LocalBookFiles.AnyAsync(f => f.BookId == id, ct);
        return Ok(new { book.Id, book.ManuallyOwned, Owned = book.ManuallyOwned || hasLocalFiles });
    }

    public sealed record RecentReleaseRow(
        int Id,
        string Title,
        int FirstPublishYear,
        int? CoverId,
        string OpenLibraryWorkKey,
        int AuthorId,
        string AuthorName,
        int AuthorPriority,
        bool Owned);

    // Books from starred authors (Priority >= 1) published in the last 12 months,
    // sorted by year descending then title. Because only the year is stored,
    // "last 12 months" is approximated as currentYear - 1 or newer.
    [HttpGet("recent-releases")]
    public async Task<IReadOnlyList<RecentReleaseRow>> RecentReleases(CancellationToken ct)
    {
        var cutoffYear = DateTime.UtcNow.Year - 1;

        return await _db.Books
            .AsNoTracking()
            .Where(b => b.Author.Priority >= 1
                     && b.FirstPublishYear != null
                     && b.FirstPublishYear >= cutoffYear)
            .OrderByDescending(b => b.FirstPublishYear)
            .ThenBy(b => b.Title)
            .Select(b => new RecentReleaseRow(
                b.Id,
                b.Title,
                b.FirstPublishYear!.Value,
                b.CoverId,
                b.OpenLibraryWorkKey,
                b.AuthorId,
                b.Author.Name,
                b.Author.Priority,
                b.ManuallyOwned || b.LocalFiles.Any()))
            .ToListAsync(ct);
    }
}

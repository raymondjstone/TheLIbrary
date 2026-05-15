using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;

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

    public sealed record BulkOwnershipRequest(IReadOnlyList<int> Ids, bool Owned);

    [HttpPost("bulk-ownership")]
    public async Task<IActionResult> BulkSetOwnership([FromBody] BulkOwnershipRequest body, CancellationToken ct)
    {
        if (body.Ids is null || body.Ids.Count == 0) return BadRequest(new { error = "Ids required" });
        var now = DateTime.UtcNow;
        await _db.Books
            .Where(b => body.Ids.Contains(b.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.ManuallyOwned, _ => body.Owned)
                .SetProperty(b => b.ManuallyOwnedAt, _ => body.Owned ? now : null), ct);
        return NoContent();
    }

    public sealed record ReadStatusRequest(ReadStatus Status, DateTime? ReadAt);

    [HttpPut("{id:int}/read-status")]
    public async Task<IActionResult> SetReadStatus(int id, [FromBody] ReadStatusRequest body, CancellationToken ct)
    {
        var book = await _db.Books.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (book is null) return NotFound();
        book.ReadStatus = body.Status;
        book.ReadAt = body.Status == ReadStatus.Read ? (body.ReadAt ?? DateTime.UtcNow) : null;
        await _db.SaveChangesAsync(ct);
        return Ok(new { book.Id, book.ReadStatus, book.ReadAt });
    }

    public sealed record WantedRequest(bool Wanted);

    [HttpPut("{id:int}/wanted")]
    public async Task<IActionResult> SetWanted(int id, [FromBody] WantedRequest body, CancellationToken ct)
    {
        var book = await _db.Books.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (book is null) return NotFound();
        book.Wanted = body.Wanted;
        await _db.SaveChangesAsync(ct);
        return Ok(new { book.Id, book.Wanted });
    }

    public sealed record DuplicateGroup(
        int BookId,
        string Title,
        int AuthorId,
        string AuthorName,
        IReadOnlyList<string> Paths);

    // Books where more than one LocalBookFile row is linked to the same Book.Id.
    [HttpGet("duplicates")]
    public async Task<IReadOnlyList<DuplicateGroup>> Duplicates(CancellationToken ct)
    {
        var groups = await _db.LocalBookFiles
            .AsNoTracking()
            .Where(f => f.BookId != null)
            .GroupBy(f => f.BookId!.Value)
            .Where(g => g.Count() > 1)
            .Select(g => new { BookId = g.Key, Paths = g.Select(f => f.FullPath).ToList() })
            .ToListAsync(ct);

        if (groups.Count == 0) return Array.Empty<DuplicateGroup>();

        var ids = groups.Select(g => g.BookId).ToList();
        var books = await _db.Books.AsNoTracking()
            .Where(b => ids.Contains(b.Id))
            .Select(b => new { b.Id, b.Title, b.AuthorId, b.Author.Name })
            .ToDictionaryAsync(b => b.Id, ct);

        return groups
            .Where(g => books.ContainsKey(g.BookId))
            .Select(g => new DuplicateGroup(
                g.BookId,
                books[g.BookId].Title,
                books[g.BookId].AuthorId,
                books[g.BookId].Name,
                g.Paths))
            .OrderBy(g => g.AuthorName).ThenBy(g => g.Title)
            .ToList();
    }

    // All distinct genre-like subjects across the library, sorted by frequency.
    [HttpGet("genres")]
    public async Task<IReadOnlyList<object>> Genres(CancellationToken ct)
    {
        var subjects = await _db.Books.AsNoTracking()
            .Where(b => b.Subjects != null && b.Subjects != "")
            .Select(b => b.Subjects!)
            .ToListAsync(ct);

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in subjects)
            foreach (var tag in row.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                counts[tag] = counts.TryGetValue(tag, out var n) ? n + 1 : 1;

        return counts.OrderByDescending(kv => kv.Value)
            .Select(kv => (object)new { genre = kv.Key, count = kv.Value })
            .ToList();
    }

    public sealed record MissingWorkRow(
        int Id,
        string Title,
        int? FirstPublishYear,
        int? CoverId,
        string OpenLibraryWorkKey,
        int AuthorId,
        string AuthorName,
        int AuthorPriority,
        bool Wanted,
        string? Subjects,
        string? Series);

    // All books from starred authors (Priority >= 1) that the user doesn't own,
    // sorted by author priority descending so the most-wanted gaps appear first.
    [HttpGet("missing")]
    public async Task<IReadOnlyList<MissingWorkRow>> MissingWorks(CancellationToken ct)
    {
        return await _db.Books
            .AsNoTracking()
            .Where(b => b.Author.Priority >= 1
                     && !b.ManuallyOwned
                     && !b.LocalFiles.Any())
            .OrderByDescending(b => b.Wanted)
            .ThenByDescending(b => b.Author.Priority)
            .ThenBy(b => b.Author.Name)
            .ThenBy(b => b.FirstPublishYear ?? int.MaxValue)
            .ThenBy(b => b.Title)
            .Select(b => new MissingWorkRow(
                b.Id,
                b.Title,
                b.FirstPublishYear,
                b.CoverId,
                b.OpenLibraryWorkKey,
                b.AuthorId,
                b.Author.Name,
                b.Author.Priority,
                b.Wanted,
                b.Subjects,
                b.Series))
            .ToListAsync(ct);
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
        bool Owned,
        string? ReadStatus,
        string? Series);

    // Books from starred authors (Priority >= 1) published in the last 5 years,
    // sorted by year descending then title. Excludes books whose normalized title
    // matches an earlier work by the same author so only genuinely new titles appear.
    [HttpGet("recent-releases")]
    public Task<IReadOnlyList<RecentReleaseRow>> RecentReleases(CancellationToken ct)
        => RecentReleasesQuery(starredOnly: true, ct);

    // Same as recent-releases but includes all tracked authors, not just starred ones.
    [HttpGet("recent-releases/all")]
    public Task<IReadOnlyList<RecentReleaseRow>> RecentReleasesAll(CancellationToken ct)
        => RecentReleasesQuery(starredOnly: false, ct);

    private async Task<IReadOnlyList<RecentReleaseRow>> RecentReleasesQuery(bool starredOnly, CancellationToken ct)
    {
        var cutoffYear = DateTime.UtcNow.Year - 5;

        return await _db.Books
            .AsNoTracking()
            .Where(b => (!starredOnly || b.Author.Priority >= 1)
                     && b.FirstPublishYear != null
                     && b.FirstPublishYear >= cutoffYear
                     && (b.NormalizedTitle == null
                         || !_db.Books.Any(b2 =>
                                b2.AuthorId == b.AuthorId
                             && b2.NormalizedTitle == b.NormalizedTitle
                             && b2.Id != b.Id
                             && b2.FirstPublishYear != null
                             && b2.FirstPublishYear < b.FirstPublishYear)))
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
                b.ManuallyOwned || b.LocalFiles.Any(),
                b.ReadStatus.ToString(),
                b.Series))
            .ToListAsync(ct);
    }
}

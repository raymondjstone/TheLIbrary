using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Sync;

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

    public sealed record SeriesRequest(string? SeriesName, string? Position);

    [HttpPut("{id:int}/series")]
    public async Task<IActionResult> SetSeries(int id, [FromBody] SeriesRequest body, CancellationToken ct)
    {
        var book = await _db.Books.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (book is null) return NotFound();
        if (string.IsNullOrWhiteSpace(body.SeriesName))
        {
            book.SeriesId = null;
        }
        else
        {
            var name = body.SeriesName.Trim();
            var normalizedName = Services.Sync.TitleNormalizer.Normalize(name);
            var series = await _db.Series.FirstOrDefaultAsync(s => s.NormalizedName == normalizedName, ct);
            if (series is null)
            {
                series = new Data.Models.Series { Name = name, NormalizedName = normalizedName, PrimaryAuthorId = book.AuthorId };
                _db.Series.Add(series);
                await _db.SaveChangesAsync(ct);
            }
            else if (series.PrimaryAuthorId is null)
            {
                series.PrimaryAuthorId = book.AuthorId;
            }
            book.SeriesId = series.Id;
        }
        book.SeriesPosition = string.IsNullOrWhiteSpace(body.Position) ? null : body.Position.Trim();
        await _db.SaveChangesAsync(ct);
        return Ok(new { book.Id, book.SeriesId, book.SeriesPosition });
    }

    public sealed record WantedAuthorGroup(
        int AuthorId,
        string AuthorName,
        IReadOnlyList<WantedBookRow> Books);

    public sealed record WantedBookRow(
        int Id,
        string Title,
        int? FirstPublishYear,
        string? Series,
        string? SeriesPosition,
        string OpenLibraryWorkKey,
        int? CoverId);

    [HttpGet("wanted")]
    public async Task<IReadOnlyList<WantedAuthorGroup>> GetWanted(CancellationToken ct)
    {
        var rows = await _db.Books.AsNoTracking()
            .Where(b => b.Wanted)
            .OrderBy(b => b.Author!.Name)
            .ThenBy(b => b.Series!.Name)
            .ThenBy(b => b.SeriesPosition)
            .ThenBy(b => b.FirstPublishYear ?? int.MaxValue)
            .ThenBy(b => b.Title)
            .Select(b => new
            {
                AuthorId = b.Author!.Id,
                AuthorName = b.Author.Name,
                b.Id, b.Title, b.FirstPublishYear,
                SeriesName = b.Series != null ? b.Series.Name : null,
                b.SeriesPosition,
                b.OpenLibraryWorkKey, b.CoverId
            })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => new { r.AuthorId, r.AuthorName })
            .Select(g => new WantedAuthorGroup(
                g.Key.AuthorId, g.Key.AuthorName,
                g.Select(b => new WantedBookRow(
                    b.Id, b.Title, b.FirstPublishYear, b.SeriesName, b.SeriesPosition,
                    b.OpenLibraryWorkKey, b.CoverId)).ToList()))
            .ToList();
    }

    public sealed record SeriesEntry(
        int Id,
        string Name,
        int? PrimaryAuthorId,
        string? PrimaryAuthorName,
        int BookCount,
        int OwnedCount,
        IReadOnlyList<SeriesBookRow> Books);

    public sealed record SeriesBookRow(
        int Id,
        string Title,
        string? SeriesPosition,
        int? FirstPublishYear,
        int? CoverId,
        string OpenLibraryWorkKey,
        int AuthorId,
        string AuthorName,
        bool Owned,
        string ReadStatus);

    [HttpGet("series")]
    public async Task<IReadOnlyList<SeriesEntry>> AllSeries(CancellationToken ct)
    {
        var series = await _db.Series
            .Include(s => s.PrimaryAuthor)
            .Include(s => s.Books).ThenInclude(b => b.Author)
            .Include(s => s.Books).ThenInclude(b => b.LocalFiles)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

        return series.Select(s =>
        {
            var books = s.Books
                .OrderBy(b => TryParsePos(b.SeriesPosition))
                .ThenBy(b => b.FirstPublishYear ?? int.MaxValue)
                .ThenBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
                .Select(b => new SeriesBookRow(
                    b.Id, b.Title, b.SeriesPosition, b.FirstPublishYear, b.CoverId,
                    b.OpenLibraryWorkKey, b.AuthorId, b.Author.Name,
                    b.ManuallyOwned || b.LocalFiles.Any(), b.ReadStatus.ToString()))
                .ToList();
            return new SeriesEntry(s.Id, s.Name, s.PrimaryAuthorId,
                s.PrimaryAuthor?.Name, books.Count, books.Count(b => b.Owned), books);
        }).ToList();
    }

    private static double TryParsePos(string? pos)
        => double.TryParse(pos, System.Globalization.NumberStyles.Any,
               System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : double.MaxValue;

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
                b.Series != null ? b.Series.Name : null))
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
        string? Series,
        string? Subjects);

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

        // Fetch all recent books with a simple range scan — no correlated subquery.
        // Deduplication (same title, same author → keep earliest) is done in memory.
        var rows = await _db.Books
            .AsNoTracking()
            .Where(b => (!starredOnly || b.Author.Priority >= 1)
                     && b.FirstPublishYear != null
                     && b.FirstPublishYear >= cutoffYear)
            .Select(b => new
            {
                b.Id, b.Title, b.NormalizedTitle, b.FirstPublishYear, b.CoverId,
                b.OpenLibraryWorkKey, b.AuthorId, b.Subjects,
                SeriesName = b.Series != null ? b.Series.Name : null,
                AuthorName = b.Author.Name, AuthorPriority = b.Author.Priority,
                Owned = b.ManuallyOwned || b.LocalFiles.Any(),
                ReadStatusStr = b.ReadStatus.ToString(),
            })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.NormalizedTitle is null
                ? $"\0{r.Id}"
                : $"{r.AuthorId}\0{r.NormalizedTitle}")
            .Select(g => g.MinBy(r => r.FirstPublishYear)!)
            .OrderByDescending(r => r.FirstPublishYear)
            .ThenBy(r => r.Title)
            .Select(r => new RecentReleaseRow(
                r.Id, r.Title, r.FirstPublishYear!.Value, r.CoverId,
                r.OpenLibraryWorkKey, r.AuthorId, r.AuthorName, r.AuthorPriority,
                r.Owned, r.ReadStatusStr, r.SeriesName, r.Subjects))
            .ToList();
    }
}

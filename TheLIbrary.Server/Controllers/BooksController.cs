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
    private readonly IHttpClientFactory _httpFactory;

    public BooksController(LibraryDbContext db, IHttpClientFactory httpFactory)
    {
        _db = db;
        _httpFactory = httpFactory;
    }

    public sealed record UpdateBookRequest(string? Title, int? FirstPublishYear, int? AuthorId);

    // Edits a book's title, publish year and/or author. Author reassignment is
    // rejected when the target author already has this work (the per-author
    // work-key uniqueness index would otherwise blow up).
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateBookRequest body, CancellationToken ct)
    {
        var book = await _db.Books.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (book is null) return NotFound();

        if (body.FirstPublishYear is int y && (y < 1 || y > DateTime.UtcNow.Year + 5))
            return BadRequest(new { error = $"First publish year looks implausible: {y}" });

        if (body.AuthorId is int newAuthorId && newAuthorId != book.AuthorId)
        {
            var target = await _db.Authors.FirstOrDefaultAsync(a => a.Id == newAuthorId, ct);
            if (target is null) return BadRequest(new { error = "Target author not found" });
            var clash = await _db.Books.AnyAsync(
                b => b.AuthorId == newAuthorId
                  && b.OpenLibraryWorkKey == book.OpenLibraryWorkKey
                  && b.Id != id, ct);
            if (clash) return Conflict(new { error = "The target author already has this work." });
            book.AuthorId = newAuthorId;
        }

        if (!string.IsNullOrWhiteSpace(body.Title))
        {
            var title = body.Title.Trim();
            book.Title = title;
            book.NormalizedTitle = Services.Sync.TitleNormalizer.Normalize(title);
        }
        book.FirstPublishYear = body.FirstPublishYear;

        await _db.SaveChangesAsync(ct);
        return Ok(new { book.Id, book.Title, book.FirstPublishYear, book.AuthorId });
    }

    // Deletes a book. Any local files linked to it fall back to unmatched
    // (the LocalBookFile→Book FK is SetNull); the author and series are kept.
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var book = await _db.Books.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (book is null) return NotFound();
        _db.Books.Remove(book);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    public sealed record ManualBookRow(
        int Id, string Title, int? FirstPublishYear, int? CoverId, string? CoverUrl,
        string? Series, string? SeriesPosition, int AuthorId, string AuthorName,
        bool Owned, string OpenLibraryWorkKey);

    // Every manually-added book — synthetic "XX" work key, not (yet) on
    // OpenLibrary. Surfaces them as a group so they can be reviewed, edited
    // or deleted from one place.
    [HttpGet("manual")]
    public async Task<IReadOnlyList<ManualBookRow>> Manual(CancellationToken ct)
    {
        return await _db.Books.AsNoTracking()
            .Where(b => b.OpenLibraryWorkKey.StartsWith(ManualWorkKey.Prefix))
            .OrderBy(b => b.Author.Name).ThenBy(b => b.Title)
            .Select(b => new ManualBookRow(
                b.Id, b.Title, b.FirstPublishYear, b.CoverId, b.CoverUrl,
                b.Series != null ? b.Series.Name : null, b.SeriesPosition,
                b.AuthorId, b.Author.Name,
                b.ManuallyOwned || b.LocalFiles.Any(), b.OpenLibraryWorkKey))
            .ToListAsync(ct);
    }

    public sealed record SetCoverRequest(string? Url);

    // Sets (or clears, with an empty URL) a custom cover image for a book —
    // mainly for manual books that have no OpenLibrary cover.
    [HttpPut("{id:int}/cover")]
    public async Task<IActionResult> SetCover(int id, [FromBody] SetCoverRequest body, CancellationToken ct)
    {
        var book = await _db.Books.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (book is null) return NotFound();

        var url = body.Url?.Trim();
        if (!string.IsNullOrEmpty(url) && !Uri.TryCreate(url, UriKind.Absolute, out _))
            return BadRequest(new { error = "Cover must be an absolute URL." });
        book.CoverUrl = string.IsNullOrEmpty(url) ? null : url;
        await _db.SaveChangesAsync(ct);
        return Ok(new { book.Id, book.CoverUrl });
    }

    public sealed record CoverCandidate(string ThumbnailUrl, string Title, string? Authors);

    // Cover-image candidates from Google Books (keyless volume search).
    // Best-effort: any failure returns an empty list rather than an error.
    [HttpGet("cover-search")]
    public async Task<IReadOnlyList<CoverCandidate>> CoverSearch(
        [FromQuery] string q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q)) return Array.Empty<CoverCandidate>();
        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            var url = $"https://www.googleapis.com/books/v1/volumes?maxResults=8&q={Uri.EscapeDataString(q)}";
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return Array.Empty<CoverCandidate>();

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("items", out var items)) return Array.Empty<CoverCandidate>();

            var result = new List<CoverCandidate>();
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("volumeInfo", out var vi)) continue;
                if (!vi.TryGetProperty("imageLinks", out var links)) continue;
                var thumb = links.TryGetProperty("thumbnail", out var t) ? t.GetString()
                          : links.TryGetProperty("smallThumbnail", out var st) ? st.GetString()
                          : null;
                if (string.IsNullOrEmpty(thumb)) continue;
                // Google serves http thumbnails — force https so the page doesn't block them.
                thumb = thumb.Replace("http://", "https://");

                var title = vi.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "";
                string? authors = null;
                if (vi.TryGetProperty("authors", out var au)
                    && au.ValueKind == System.Text.Json.JsonValueKind.Array)
                    authors = string.Join(", ", au.EnumerateArray()
                        .Select(a => a.GetString()).Where(s => !string.IsNullOrEmpty(s)));
                result.Add(new CoverCandidate(thumb, title, authors));
            }
            return result;
        }
        catch
        {
            return Array.Empty<CoverCandidate>();
        }
    }

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
        int? ParentSeriesId,
        string? ParentSeriesName,
        string? PositionInParent,
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

        // Resolve parent names from the already-loaded list to avoid a self-referential Include
        // combining with multi-collection includes (Books.Author + Books.LocalFiles).
        var nameById = series.ToDictionary(s => s.Id, s => s.Name);

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
            var parentName = s.ParentSeriesId.HasValue
                && nameById.TryGetValue(s.ParentSeriesId.Value, out var pn) ? pn : null;
            return new SeriesEntry(s.Id, s.Name, s.PrimaryAuthorId, s.PrimaryAuthor?.Name,
                s.ParentSeriesId, parentName, s.PositionInParent,
                books.Count, books.Count(b => b.Owned), books);
        }).ToList();
    }

    private static double TryParsePos(string? pos)
        => double.TryParse(pos, System.Globalization.NumberStyles.Any,
               System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : double.MaxValue;

    public sealed record DuplicateFile(int Id, string Path, string? Format);

    public sealed record DuplicateGroup(
        int BookId,
        string Title,
        int AuthorId,
        string AuthorName,
        IReadOnlyList<DuplicateFile> Files,
        // Format the user probably wants to keep (epub > pdf > mobi > others).
        // The UI can highlight files that are NOT this format as upgrade candidates.
        string? RecommendedFormat,
        IReadOnlyList<string> Paths);  // kept for backwards compatibility with older clients

    // Preference order — earlier = better. Matched against
    // Path.GetExtension(...).TrimStart('.').ToLowerInvariant().
    private static readonly string[] FormatPreference =
        new[] { "epub", "pdf", "azw3", "mobi", "azw", "fb2", "lit", "cbz", "docx", "odt", "prc", "pdb" };

    // Books where more than one LocalBookFile row is linked to the same Book.Id.
    // When `authorId` is provided, only that author's books are returned (used
    // for the per-author drilldown on the author detail page).
    [HttpGet("duplicates")]
    public async Task<IReadOnlyList<DuplicateGroup>> Duplicates(
        CancellationToken ct, [FromQuery] int? authorId = null)
    {
        var query = _db.LocalBookFiles
            .AsNoTracking()
            .Where(f => f.BookId != null);
        if (authorId is int aid)
            query = query.Where(f => f.AuthorId == aid);

        var groups = await query
            .GroupBy(f => f.BookId!.Value)
            .Where(g => g.Count() > 1)
            .Select(g => new { BookId = g.Key, Files = g.Select(f => new { f.Id, f.FullPath }).ToList() })
            .ToListAsync(ct);

        if (groups.Count == 0) return Array.Empty<DuplicateGroup>();

        var ids = groups.Select(g => g.BookId).ToList();
        var books = await _db.Books.AsNoTracking()
            .Where(b => ids.Contains(b.Id))
            .Select(b => new { b.Id, b.Title, b.AuthorId, b.Author.Name })
            .ToDictionaryAsync(b => b.Id, ct);

        return groups
            .Where(g => books.ContainsKey(g.BookId))
            .Select(g =>
            {
                var formatted = g.Files
                    .Select(f => new DuplicateFile(
                        f.Id, f.FullPath,
                        FormatOf(f.FullPath)))
                    .ToList();
                var recommended = formatted
                    .Select(f => f.Format)
                    .Where(f => f is not null)
                    .OrderBy(f => Array.IndexOf(FormatPreference, f))
                    .FirstOrDefault();
                return new DuplicateGroup(
                    g.BookId,
                    books[g.BookId].Title,
                    books[g.BookId].AuthorId,
                    books[g.BookId].Name,
                    formatted,
                    recommended,
                    formatted.Select(f => f.Path).ToList());
            })
            .OrderBy(g => g.AuthorName).ThenBy(g => g.Title)
            .ToList();
    }

    private static string? FormatOf(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return null;
        var ext = Path.GetExtension(fullPath).TrimStart('.').ToLowerInvariant();
        return string.IsNullOrEmpty(ext) ? null : ext;
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

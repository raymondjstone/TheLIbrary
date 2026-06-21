using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.IO;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BooksController : ControllerBase
{
    private readonly LibraryDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IFileSystem _fs;

    public BooksController(LibraryDbContext db, IHttpClientFactory httpFactory, IFileSystem fs)
    {
        _db = db;
        _httpFactory = httpFactory;
        _fs = fs;
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
                b.ManuallyOwned || b.OwnedDifferentEdition || b.LocalFiles.Any(), b.OpenLibraryWorkKey))
            .ToListAsync(ct);
    }

    public sealed class SetCoverRequest : IValidatableObject
    {
        [StringLength(1024)]
        public string? Url { get; init; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (string.IsNullOrWhiteSpace(Url)) yield break;

            var trimmed = Url.Trim();
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                yield return new ValidationResult(
                    "Cover must be an absolute http/https URL.",
                    new[] { nameof(Url) });
            }
        }
    }

    // Sets (or clears, with an empty URL) a custom cover image for a book —
    // mainly for manual books that have no OpenLibrary cover.
    [HttpPut("{id:int}/cover")]
    public async Task<IActionResult> SetCover(int id, [FromBody] SetCoverRequest body, CancellationToken ct)
    {
        var book = await _db.Books.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (book is null) return NotFound();

        var url = body.Url?.Trim();
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

    // "Got but in a different edition" — the user already has this work as some
    // other edition than what's catalogued, with no local file here. Counts as
    // owned everywhere (so the book leaves Missing / unowned views), independent
    // of the physical ManuallyOwned flag. Setting it true also clears Wanted —
    // you have the book now, so it must drop off the Wanted list (which lists
    // every Wanted row regardless of ownership).
    [HttpPost("{id:int}/owned-different-edition")]
    public async Task<IActionResult> SetOwnedDifferentEdition(int id, [FromBody] OwnershipRequest body, CancellationToken ct)
    {
        var book = await _db.Books.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (book is null) return NotFound();

        book.OwnedDifferentEdition = body.Owned;
        book.OwnedDifferentEditionAt = body.Owned ? DateTime.UtcNow : null;
        if (body.Owned) book.Wanted = false;
        await _db.SaveChangesAsync(ct);

        var hasLocalFiles = await _db.LocalBookFiles.AnyAsync(f => f.BookId == id, ct);
        return Ok(new
        {
            book.Id,
            book.OwnedDifferentEdition,
            book.Wanted,
            Owned = book.ManuallyOwned || book.OwnedDifferentEdition || hasLocalFiles,
        });
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

    public sealed record SuppressedRequest(bool Suppressed);

    [HttpPut("{id:int}/suppressed")]
    public async Task<IActionResult> SetSuppressed(int id, [FromBody] SuppressedRequest body, CancellationToken ct)
    {
        var book = await _db.Books.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (book is null) return NotFound();
        book.Suppressed = body.Suppressed;
        await _db.SaveChangesAsync(ct);
        return Ok(new { book.Id, book.Suppressed });
    }

    public sealed record ForeignRequest(bool Foreign);

    // Marks a single book as foreign (not in English) or clears the flag.
    // Foreign always implies Suppressed, so the two move together. Clearing the
    // flag ("not foreign") is a sticky decision: it records ConfirmedEnglish so
    // the automatic foreign scan will never re-flag or re-suppress this book.
    [HttpPut("{id:int}/foreign")]
    public async Task<IActionResult> SetForeign(int id, [FromBody] ForeignRequest body, CancellationToken ct)
    {
        var book = await _db.Books.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (book is null) return NotFound();
        book.Foreign = body.Foreign;
        book.Suppressed = body.Foreign;
        book.LanguageReview = body.Foreign ? LanguageReview.None : LanguageReview.ConfirmedEnglish;
        await _db.SaveChangesAsync(ct);
        return Ok(new { book.Id, book.Foreign, book.Suppressed, LanguageReview = book.LanguageReview.ToString() });
    }

    public sealed record ConfirmForeignRequest(bool Confirmed);

    // Confirms (or un-confirms) that a foreign book really is foreign. The book
    // stays foreign + suppressed either way; this only records that a human has
    // reviewed it, which sorts it to the bottom of the Foreign Titles list.
    [HttpPut("{id:int}/foreign/confirm")]
    public async Task<IActionResult> ConfirmForeign(int id, [FromBody] ConfirmForeignRequest body, CancellationToken ct)
    {
        var book = await _db.Books.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (book is null) return NotFound();
        if (!book.Foreign) return BadRequest(new { error = "Book is not flagged foreign." });
        book.LanguageReview = body.Confirmed ? LanguageReview.ConfirmedForeign : LanguageReview.None;
        await _db.SaveChangesAsync(ct);
        return Ok(new { book.Id, LanguageReview = book.LanguageReview.ToString() });
    }

    public sealed record ConfirmAllForeignResult(int Confirmed);

    // Marks every currently foreign-but-unconfirmed book as confirmed foreign in
    // one shot — the "Confirm all listed as foreign" action on the review page.
    [HttpPost("foreign/confirm-all")]
    public async Task<ActionResult<ConfirmAllForeignResult>> ConfirmAllForeign(CancellationToken ct)
    {
        var confirmed = await _db.Books
            .Where(b => b.Foreign && b.LanguageReview != LanguageReview.ConfirmedForeign)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.LanguageReview, LanguageReview.ConfirmedForeign), ct);
        return Ok(new ConfirmAllForeignResult(confirmed));
    }

    public sealed record ForeignBookRow(
        int Id, string Title, int? FirstPublishYear, int? CoverId, string? CoverUrl,
        int AuthorId, string AuthorName, int AuthorPriority, string OpenLibraryWorkKey, bool Confirmed);

    // Every book currently flagged foreign, for the Foreign Titles review page.
    // Un-reviewed (auto-flagged) books come first; user-confirmed ones last.
    [HttpGet("foreign")]
    public async Task<IReadOnlyList<ForeignBookRow>> Foreign(CancellationToken ct)
    {
        return await _db.Books.AsNoTracking()
            .Where(b => b.Foreign)
            .OrderBy(b => b.LanguageReview == LanguageReview.ConfirmedForeign)
            .ThenBy(b => b.Author.Name).ThenBy(b => b.Title)
            .Select(b => new ForeignBookRow(
                b.Id, b.Title, b.FirstPublishYear, b.CoverId, b.CoverUrl,
                b.AuthorId, b.Author.Name, b.Author.Priority, b.OpenLibraryWorkKey,
                b.LanguageReview == LanguageReview.ConfirmedForeign))
            .ToListAsync(ct);
    }

    public sealed record ForeignScanResult(int Scanned, int Flagged);

    // Runs the title-language guesser over every book not already flagged
    // foreign and flags (+ suppresses) the ones it is confident are not in
    // English. Conservative by design: ambiguous titles are left untouched, and
    // books the user has confirmed English are permanently skipped.
    [HttpPost("foreign/scan")]
    public async Task<ActionResult<ForeignScanResult>> ScanForeign(CancellationToken ct)
    {
        var candidates = await _db.Books
            .Where(b => !b.Foreign && b.LanguageReview != LanguageReview.ConfirmedEnglish)
            .Select(b => new { b.Id, b.Title })
            .ToListAsync(ct);

        var flagIds = candidates
            .Where(b => TitleLanguageGuesser.IsLikelyNonEnglish(b.Title))
            .Select(b => b.Id)
            .ToList();

        if (flagIds.Count > 0)
        {
            await _db.Books
                .Where(b => flagIds.Contains(b.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Foreign, true)
                    .SetProperty(b => b.Suppressed, true), ct);
        }

        return Ok(new ForeignScanResult(candidates.Count, flagIds.Count));
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
        IReadOnlyList<SeriesBookRow> Books,
        bool GapsInSequence = false,
        string? GapsDescription = null);

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
                    b.ManuallyOwned || b.OwnedDifferentEdition || b.LocalFiles.Any(), b.ReadStatus.ToString()))
                .ToList();

            // Calculate gaps: look for numeric representation positions in books, detect missing integers in the owned sequence.
            // e.g. if we have owned 1, 3, then 2 is missing (and is a gap inside the sequence). Or 1, 2, 4 (missing 3).
            // A gap exists only if we own some higher number but have missing lower numbers (that we don't own).
            var gapsInSequence = false;
            string? gapsDescription = null;

            var parsedPosList = books
                .Select(b => {
                    var ok = double.TryParse(b.SeriesPosition, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val);
                    return new { Book = b, Valid = ok, Value = val };
                })
                .Where(x => x.Valid)
                .OrderBy(x => x.Value)
                .ToList();

            if (parsedPosList.Count > 0)
            {
                var ownedPositions = parsedPosList.Where(x => x.Book.Owned).Select(x => x.Value).ToHashSet();
                if (ownedPositions.Count > 0)
                {
                    var maxOwned = ownedPositions.Max();
                    var missingList = new List<double>();
                    // Look for any defined book positions up to the maximum owned position that are NOT owned
                    foreach (var p in parsedPosList)
                    {
                        if (p.Value < maxOwned && !p.Book.Owned)
                        {
                            missingList.Add(p.Value);
                        }
                    }

                    if (missingList.Count > 0)
                    {
                        gapsInSequence = true;
                        gapsDescription = string.Join(", ", missingList.Select(v => $"#{v}"));
                    }
                }
            }

            var parentName = s.ParentSeriesId.HasValue
                && nameById.TryGetValue(s.ParentSeriesId.Value, out var pn) ? pn : null;
            return new SeriesEntry(s.Id, s.Name, s.PrimaryAuthorId, s.PrimaryAuthor?.Name,
                s.ParentSeriesId, parentName, s.PositionInParent,
                books.Count, books.Count(b => b.Owned), books,
                gapsInSequence, gapsDescription);
        }).ToList();
    }

    private static double TryParsePos(string? pos)
        => double.TryParse(pos, System.Globalization.NumberStyles.Any,
               System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : double.MaxValue;

    // IntegrityOk: null = not yet checked by the integrity job, true = healthy,
    // false = flagged damaged. Lets the UI show a per-copy status and never keep
    // a damaged copy when a healthy one exists.
    public sealed record DuplicateFile(int Id, string Path, string? Format, bool? IntegrityOk);

    public sealed record DuplicateGroup(
        int BookId,
        string Title,
        int AuthorId,
        string AuthorName,
        IReadOnlyList<DuplicateFile> Files,
        // Format the user probably wants to keep (epub > pdf > mobi > others).
        // The UI can highlight files that are NOT this format as upgrade candidates.
        string? RecommendedFormat,
        // The specific copy to keep: a non-damaged file wins over the format
        // preference, so a damaged copy is never the keeper unless every copy is.
        int RecommendedFileId,
        IReadOnlyList<string> Paths);  // kept for backwards compatibility with older clients

    // Preference order — earlier = better. Shared with the Archived Files page so
    // "best copy to keep" and "best copy to restore" rank formats identically.
    public static readonly string[] DefaultFormatPreference =
        TheLibrary.Server.Services.FormatPreference.Default;

    // Books where more than one LocalBookFile row is linked to the same Book.Id.
    // When `authorId` is provided, only that author's books are returned (used
    // for the per-author drilldown on the author detail page).
    [HttpGet("duplicates")]
    public async Task<IReadOnlyList<DuplicateGroup>> Duplicates(
        CancellationToken ct, [FromQuery] int? authorId = null, [FromQuery] bool starredOnly = false)
    {
        var preference = await GetFormatPreferenceAsync(ct);
        var query = _db.LocalBookFiles
            .AsNoTracking()
            .Where(f => f.BookId != null);
        if (authorId is int aid)
            query = query.Where(f => f.AuthorId == aid);
        // Starred = priority author (Priority >= 1), matching the rest of the app.
        if (starredOnly)
            query = query.Where(f => f.Author != null && f.Author.Priority >= 1);

        // Exclude files that already live under the archive folder, otherwise a
        // book whose extras were archived (rows kept, only FullPath changed)
        // would keep showing up as a duplicate group with nothing changed.
        var archiveStored = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.DedupeArchiveFolder)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        // Match on '/' explicitly (stored paths are forward-slash on the Linux
        // mount), not Path.DirectorySeparatorChar which is '\' on a Windows host.
        var archiveLeaf = (string.IsNullOrWhiteSpace(archiveStored) ? "__archive" : archiveStored.Trim())
            .Replace('\\', '/').TrimEnd('/');
        if (archiveLeaf.Contains('/'))
        {
            var prefix = archiveLeaf + "/";
            query = query.Where(f => !f.FullPath.StartsWith(prefix));
        }
        else
        {
            var segment = "/" + archiveLeaf + "/";
            query = query.Where(f => !f.FullPath.Contains(segment));
        }

        var groups = await query
            .GroupBy(f => f.BookId!.Value)
            .Where(g => g.Count() > 1)
            .Select(g => new { BookId = g.Key, Files = g.Select(f => new { f.Id, f.FullPath, f.IntegrityOk }).ToList() })
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
                // Resolve each row to a real, actionable copy. A folder-shaped
                // row only counts if it actually holds a readable ebook file —
                // this drops stale/empty title-folder pointers that would
                // otherwise show a bare directory as a duplicate "copy".
                var formatted = g.Files
                    .Select(f => (File: f, Copy: ResolveDuplicateCopy(f.FullPath, preference)))
                    .Where(x => x.Copy.IsRealCopy)
                    .Select(x => new DuplicateFile(x.File.Id, x.File.FullPath, x.Copy.Format, x.File.IntegrityOk))
                    .ToList();
                return (BookId: g.BookId, Files: formatted);
            })
            // After dropping non-copies a group may fall back to a single copy —
            // it's no longer a duplicate, so exclude it.
            .Where(g => g.Files.Count > 1)
            .Select(g =>
            {
                // The keeper: a non-damaged copy always beats a damaged one;
                // among equals, the preferred format wins, then the lowest id.
                var keeper = g.Files
                    .OrderBy(f => f.IntegrityOk == false ? 1 : 0)
                    .ThenBy(f => PreferenceRank(f.Format, preference))
                    .ThenBy(f => f.Id)
                    .First();
                return new DuplicateGroup(
                    g.BookId,
                    books[g.BookId].Title,
                    books[g.BookId].AuthorId,
                    books[g.BookId].Name,
                    g.Files,
                    keeper.Format,
                    keeper.Id,
                    g.Files.Select(f => f.Path).ToList());
            })
            .OrderBy(g => g.AuthorName).ThenBy(g => g.Title)
            .ToList();
    }

    public sealed record DuplicateActionRequest(IReadOnlyList<int> FileIds, string Action, string? ArchiveFolderName);
    public sealed record DuplicateActionResult(int Deleted, int Archived, IReadOnlyList<string> Warnings);

    [HttpPost("duplicates/actions")]
    public async Task<ActionResult<DuplicateActionResult>> ApplyDuplicateAction(
        [FromBody] DuplicateActionRequest body,
        CancellationToken ct)
    {
        if (body.FileIds is null || body.FileIds.Count == 0)
            return BadRequest(new { error = "At least one file id is required." });

        var action = body.Action?.Trim().ToLowerInvariant();
        if (action is not ("delete" or "archive"))
            return BadRequest(new { error = "Action must be 'delete' or 'archive'." });

        var files = await _db.LocalBookFiles
            .Where(f => body.FileIds.Contains(f.Id))
            .ToListAsync(ct);
        if (files.Count == 0) return NotFound(new { error = "No matching files found." });

        var warnings = new List<string>();
        var locations = await _db.LibraryLocations.AsNoTracking()
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);

        // Prefer whatever the client sent, but fall back to the persisted
        // DedupeArchiveFolder setting, then finally "__archive".
        string archiveLeaf;
        if (!string.IsNullOrWhiteSpace(body.ArchiveFolderName))
        {
            archiveLeaf = body.ArchiveFolderName.Trim();
        }
        else
        {
            var stored = await _db.AppSettings.AsNoTracking()
                .Where(s => s.Key == AppSettingKeys.DedupeArchiveFolder)
                .Select(s => s.Value)
                .FirstOrDefaultAsync(ct);
            archiveLeaf = string.IsNullOrWhiteSpace(stored) ? "__archive" : stored.Trim();
        }
        var deleted = 0;
        var archived = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(file.FullPath))
            {
                warnings.Add($"#{file.Id}: no path recorded.");
                continue;
            }

            if (action == "delete")
            {
                try
                {
                    if (await _fs.FileExistsAsync(file.FullPath, ct))
                    {
                        await _fs.DeleteFileAsync(file.FullPath, ct);
                        deleted++;
                    }
                    else if (await _fs.DirectoryExistsAsync(file.FullPath, ct))
                    {
                        await _fs.DeleteDirectoryAsync(file.FullPath, recursive: true, ct);
                        deleted++;
                    }
                    else warnings.Add($"#{file.Id}: path no longer exists on disk.");
                    _db.LocalBookFiles.Remove(file);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    warnings.Add($"#{file.Id}: {ex.Message}");
                }
                continue;
            }

            var location = locations.FirstOrDefault(l =>
                file.FullPath.StartsWith(l.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
            if (location is null)
            {
                warnings.Add($"#{file.Id}: file is outside enabled library roots.");
                continue;
            }

            var libRoot = location.Replace('\\', '/').TrimEnd('/');
            var relative = file.FullPath.Replace('\\', '/')[libRoot.Length..].TrimStart('/');
            // archiveLeaf may be a simple folder name (nested inside each library
            // root) or a full absolute path (one fixed archive location). Either
            // way the file's library-relative subfolders are preserved underneath.
            // A value containing a separator is a full path (forward-slash based,
            // to match the Linux mount regardless of the host OS this runs on).
            //
            // CRITICAL: build the destination with forward slashes, NEVER Path.Combine.
            // Stored paths are always forward-slash (the library lives on the Linux
            // mount), and the Duplicates list excludes archived rows by matching a
            // forward-slash archive prefix/segment. On a Windows host Path.Combine
            // emits '\', so the repointed row would be stored as
            // ".../TheLibrary_Archive\Author\..." and silently FAIL that exclusion —
            // the archived copy then keeps showing as a duplicate with no warning.
            var destBase = (archiveLeaf.Contains('/') || archiveLeaf.Contains('\\'))
                ? archiveLeaf.Replace('\\', '/').TrimEnd('/')
                : $"{libRoot}/{archiveLeaf}";
            var destPath = $"{destBase}/{relative}";
            var destDir = destPath[..destPath.LastIndexOf('/')];

            try
            {
                if (destDir is not null) await _fs.CreateDirectoryAsync(destDir, ct);
                if (await _fs.FileExistsAsync(file.FullPath, ct))
                {
                    var src = file.FullPath;
                    // Forward-slash the result: UniqueFileAsync uses Path.Combine,
                    // which re-introduces '\' on a Windows host (see destPath note).
                    var final = (await UniqueFileAsync(destPath, ct)).Replace('\\', '/');
                    await _fs.MoveFileAsync(src, final, overwrite: false, ct);

                    // CRITICAL: when the archive folder is a different mount from the
                    // library, File.Move is copy+delete and the source delete can
                    // silently fail — leaving the live original in place. If we then
                    // repoint the row to the archive, the surviving original gets a
                    // fresh row on the next scan and reappears as a duplicate forever
                    // (this is the root cause of the recurring archived duplicates).
                    // Verify the source is gone; force-remove it once the archived
                    // copy is confirmed present; never repoint while the original lives.
                    if (await _fs.FileExistsAsync(src, ct) && await _fs.FileExistsAsync(final, ct))
                    {
                        try { await _fs.DeleteFileAsync(src, ct); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    }
                    if (await _fs.FileExistsAsync(src, ct))
                    {
                        warnings.Add($"#{file.Id}: archived a copy but could not remove the live original at {src} — left as-is.");
                        continue;
                    }
                    file.FullPath = final;
                    archived++;
                }
                else if (await _fs.DirectoryExistsAsync(file.FullPath, ct))
                {
                    var src = file.FullPath;
                    var final = (await UniqueDirectoryAsync(Path.GetDirectoryName(destPath)!, Path.GetFileName(destPath), ct)).Replace('\\', '/');
                    await _fs.MoveDirectoryAsync(src, final, ct);
                    if (await _fs.DirectoryExistsAsync(src, ct))
                    {
                        try { await _fs.DeleteDirectoryAsync(src, recursive: true, ct); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    }
                    if (await _fs.DirectoryExistsAsync(src, ct))
                    {
                        warnings.Add($"#{file.Id}: archived a copy but could not remove the live original folder at {src} — left as-is.");
                        continue;
                    }
                    file.FullPath = final;
                    archived++;
                }
                else warnings.Add($"#{file.Id}: path no longer exists on disk.");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                warnings.Add($"#{file.Id}: {ex.Message}");
            }
        }

        if (archived > 0 || deleted > 0)
        {
            var verb = action == "archive" ? "Archived" : "Deleted";
            var n = action == "archive" ? archived : deleted;
            Services.ActivityLogger.Record(_db, action!,
                $"{verb} {n} duplicate file(s) via the Duplicates page" + (warnings.Count > 0 ? $" ({warnings.Count} warning(s))" : ""));
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new DuplicateActionResult(deleted, archived, warnings));
    }

    // Decides whether a LocalBookFile row is a real, actionable duplicate copy
    // and returns its format. A plain file is a copy (by extension). A directory
    // (classic library layout) is a copy ONLY if it holds a readable ebook file
    // — so empty/stale title-folder pointers, or folders that only contain a
    // cover, are not treated as duplicates. A path that is neither an existing
    // file nor directory is kept only if it *looks* like an ebook file (a NAS
    // hiccup shouldn't hide a real file); a phantom folder path is dropped.
    private static (string? Format, bool IsRealCopy) ResolveDuplicateCopy(
        string fullPath, IReadOnlyList<string> preference)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return (null, false);

        if (System.IO.File.Exists(fullPath))
        {
            var e = Path.GetExtension(fullPath).TrimStart('.').ToLowerInvariant();
            return (e.Length > 0 ? e : null, true);
        }

        if (Directory.Exists(fullPath))
        {
            try
            {
                var fmt = Directory.EnumerateFiles(fullPath)
                    .Where(f => Services.Calibre.CalibreScanner.EbookExtensions.Contains(Path.GetExtension(f)))
                    .Select(f => Path.GetExtension(f).TrimStart('.').ToLowerInvariant())
                    .OrderBy(e => PreferenceRank(e, preference))
                    .FirstOrDefault();
                return (fmt, fmt is not null);
            }
            catch { return (null, false); }
        }

        // Missing on disk: keep an ebook-shaped file path (conservative), drop a
        // folder-shaped one.
        var ext = Path.GetExtension(fullPath).TrimStart('.').ToLowerInvariant();
        return ext.Length > 0 && Services.Calibre.CalibreScanner.EbookExtensions.Contains("." + ext)
            ? (ext, true)
            : (null, false);
    }

    private static string? FormatOf(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return null;

        // Library layout: FullPath is a directory — find the best readable file inside it.
        // Check this BEFORE the extension fast-path because folder names like "My Book v1.5"
        // or "Title (2)" would otherwise fool Path.GetExtension into returning ".5" or ".2".
        if (Directory.Exists(fullPath))
        {
            try
            {
                var preferred = DefaultFormatPreference;
                return Directory.EnumerateFiles(fullPath)
                    .Select(f => Path.GetExtension(f).TrimStart('.').ToLowerInvariant())
                    .Where(e => !string.IsNullOrEmpty(e))
                    .OrderBy(e => PreferenceRank(e, preferred))
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        // Fast path: FullPath is a direct file (flat layout).
        var ext = Path.GetExtension(fullPath).TrimStart('.').ToLowerInvariant();
        return string.IsNullOrEmpty(ext) ? null : ext;
    }

    private async Task<string[]> GetFormatPreferenceAsync(CancellationToken ct)
    {
        var raw = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.DuplicateFormatPreference)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        var parsed = string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(f => f.TrimStart('.').ToLowerInvariant())
                .Where(f => f.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        return parsed.Length > 0 ? parsed : DefaultFormatPreference;
    }

    private static int PreferenceRank(string? format, IReadOnlyList<string> preference)
    {
        if (string.IsNullOrWhiteSpace(format)) return int.MaxValue;
        var i = -1;
        for (var idx = 0; idx < preference.Count; idx++)
        {
            if (!string.Equals(preference[idx], format, StringComparison.OrdinalIgnoreCase)) continue;
            i = idx;
            break;
        }
        return i >= 0 ? i : preference.Count + 100;
    }

    private async Task<string> UniqueFileAsync(string desired, CancellationToken ct)
    {
        if (!await _fs.FileExistsAsync(desired, ct) && !await _fs.DirectoryExistsAsync(desired, ct)) return desired;
        var dir = Path.GetDirectoryName(desired) ?? "";
        var stem = Path.GetFileNameWithoutExtension(desired);
        var ext = Path.GetExtension(desired);
        for (var i = 2; i < 1000; i++)
        {
            var next = Path.Combine(dir, $"{stem}_{i}{ext}");
            if (!await _fs.FileExistsAsync(next, ct) && !await _fs.DirectoryExistsAsync(next, ct)) return next;
        }
        return Path.Combine(dir, $"{stem}_{DateTime.UtcNow:yyyyMMddHHmmss}{ext}");
    }

    private async Task<string> UniqueDirectoryAsync(string parent, string leaf, CancellationToken ct)
    {
        var candidate = Path.Combine(parent, leaf);
        if (!await _fs.DirectoryExistsAsync(candidate, ct) && !await _fs.FileExistsAsync(candidate, ct)) return candidate;
        for (var i = 2; i < 1000; i++)
        {
            var next = Path.Combine(parent, $"{leaf} ({i})");
            if (!await _fs.DirectoryExistsAsync(next, ct) && !await _fs.FileExistsAsync(next, ct)) return next;
        }
        return Path.Combine(parent, $"{leaf} ({DateTime.UtcNow:yyyyMMddHHmmss})");
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
            .Where(b => b.Author.Priority >= 1 && !b.Suppressed)
            .Where(BookOwnership.NotOwned)
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

    public sealed record PhysicalOnlyRow(
        int Id,
        string Title,
        int? FirstPublishYear,
        int? CoverId,
        string OpenLibraryWorkKey,
        int AuthorId,
        string AuthorName,
        string ReadStatus,
        string? Series,
        string? SeriesPosition);

    // Books the user holds ONLY as a physical copy: ManuallyOwned, with no local
    // ebook file and not marked "got in a different edition". These are the gaps
    // where a digital copy is still wanted even though the work is "owned" — so
    // they're deliberately excluded from the Missing list but surfaced here.
    [HttpGet("physical-only")]
    public async Task<IReadOnlyList<PhysicalOnlyRow>> PhysicalOnly(CancellationToken ct)
    {
        return await _db.Books
            .AsNoTracking()
            .Where(b => b.ManuallyOwned
                     && !b.OwnedDifferentEdition
                     && !b.Suppressed
                     && !b.LocalFiles.Any())
            .OrderByDescending(b => b.Author.Priority)
            .ThenBy(b => b.Author.Name)
            .ThenBy(b => b.FirstPublishYear ?? int.MaxValue)
            .ThenBy(b => b.Title)
            .Select(b => new PhysicalOnlyRow(
                b.Id,
                b.Title,
                b.FirstPublishYear,
                b.CoverId,
                b.OpenLibraryWorkKey,
                b.AuthorId,
                b.Author.Name,
                b.ReadStatus.ToString(),
                b.Series != null ? b.Series.Name : null,
                b.SeriesPosition))
            .ToListAsync(ct);
    }

    public sealed record UpNextRow(
        int BookId, string Title, int AuthorId, string AuthorName,
        string Series, string? SeriesPosition, int? CoverId, string OpenLibraryWorkKey, int? LocalFileId);

    // "Up next": for every series the user has STARTED (owns and has read at least
    // one volume), the next owned-but-unread volume by reading-order position. The
    // reading queue across all in-progress series. LocalFileId (when present) lets
    // the UI send it straight to reMarkable.
    [HttpGet("up-next")]
    public async Task<IReadOnlyList<UpNextRow>> UpNext(CancellationToken ct)
    {
        // Series the user has started reading (≥1 read book in the series).
        var startedSeries = await _db.Books.AsNoTracking()
            .Where(b => b.SeriesId != null && b.ReadStatus == ReadStatus.Read)
            .Select(b => b.SeriesId!.Value).Distinct().ToListAsync(ct);
        if (startedSeries.Count == 0) return Array.Empty<UpNextRow>();
        var started = startedSeries.ToHashSet();

        // Owned, unread volumes in those series.
        var rows = await _db.Books.AsNoTracking()
            .Where(b => b.SeriesId != null && !b.Suppressed && !b.Foreign && b.ReadStatus == ReadStatus.Unread)
            .Where(BookOwnership.Owned)
            .Select(b => new
            {
                b.Id, b.Title, b.AuthorId, AuthorName = b.Author.Name, b.SeriesId,
                SeriesName = b.Series!.Name, b.SeriesPosition, b.FirstPublishYear, b.CoverId, b.OpenLibraryWorkKey,
                LocalFileId = b.LocalFiles.Select(f => (int?)f.Id).FirstOrDefault(),
            })
            .ToListAsync(ct);

        // One row per series: the lowest-position owned-unread volume.
        return rows
            .Where(r => started.Contains(r.SeriesId!.Value))
            .GroupBy(r => r.SeriesId!.Value)
            .Select(g => g
                .OrderBy(r => ParseSeriesPosition(r.SeriesPosition))
                .ThenBy(r => r.FirstPublishYear ?? int.MaxValue)
                .ThenBy(r => r.Title)
                .First())
            .OrderBy(r => r.AuthorName).ThenBy(r => r.SeriesName)
            .Select(r => new UpNextRow(
                r.Id, r.Title, r.AuthorId, r.AuthorName, r.SeriesName, r.SeriesPosition,
                r.CoverId, r.OpenLibraryWorkKey, r.LocalFileId))
            .ToList();
    }

    // Leading numeric part of a series position string ("3", "02", "10.5") for
    // ordering; non-numeric / missing positions sort last.
    private static double ParseSeriesPosition(string? pos)
    {
        if (string.IsNullOrWhiteSpace(pos)) return double.MaxValue;
        var m = System.Text.RegularExpressions.Regex.Match(pos, @"\d+(\.\d+)?");
        return m.Success && double.TryParse(m.Value, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v : double.MaxValue;
    }

    // CSV download of the missing-works list.
    [HttpGet("missing/export")]
    public async Task<IActionResult> ExportMissingWorks(CancellationToken ct)
    {
        var rows = await MissingWorks(ct);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Author,Title,Year,Series,Wanted,OpenLibraryKey");
        foreach (var r in rows)
        {
            static string Esc(string? s) => s is null ? "" : "\"" + s.Replace("\"", "\"\"") + "\"";
            sb.Append(Esc(r.AuthorName)).Append(',')
              .Append(Esc(r.Title)).Append(',')
              .Append(r.FirstPublishYear?.ToString() ?? "").Append(',')
              .Append(Esc(r.Series)).Append(',')
              .Append(r.Wanted ? "yes" : "").Append(',')
              .AppendLine(Esc(r.OpenLibraryWorkKey));
        }
        var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        return File(bytes, "text/csv", "missing-works.csv");
    }

    public sealed record BulkMarkWantedRequest(IReadOnlyList<int>? Ids, bool Wanted);

    // Marks a batch of books wanted/unwanted in one call.
    [HttpPost("bulk-wanted")]
    public async Task<IActionResult> BulkMarkWanted([FromBody] BulkMarkWantedRequest body, CancellationToken ct)
    {
        var ids = (body.Ids ?? Array.Empty<int>()).ToList();
        if (ids.Count == 0) return Ok(new { updated = 0 });
        var books = await _db.Books.Where(b => ids.Contains(b.Id)).ToListAsync(ct);
        foreach (var b in books) b.Wanted = body.Wanted;
        await _db.SaveChangesAsync(ct);
        return Ok(new { updated = books.Count });
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
        string? SeriesPosition,
        string? Subjects,
        // When the kept record was first added to the library; null for books that
        // predate added-date tracking. Drives the "by month" grouping on the page.
        DateTime? CreatedAt);

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
                     && !b.Suppressed
                     // Foreign titles never belong on the releases pages. Suppressed
                     // is meant to cover them too, but a foreign book whose Suppressed
                     // flag drifted off would otherwise leak through — exclude on the
                     // foreign flag directly.
                     && !b.Foreign
                     && b.FirstPublishYear != null
                     && b.FirstPublishYear >= cutoffYear)
            .Select(b => new
            {
                b.Id, b.Title, b.NormalizedTitle, b.FirstPublishYear, b.CoverId,
                b.OpenLibraryWorkKey, b.AuthorId, b.Subjects, b.CreatedAt, b.SeriesPosition,
                SeriesName = b.Series != null ? b.Series.Name : null,
                AuthorName = b.Author.Name, AuthorPriority = b.Author.Priority,
                Owned = b.ManuallyOwned || b.OwnedDifferentEdition || b.LocalFiles.Any(),
                ReadStatusStr = b.ReadStatus.ToString(),
            })
            .ToListAsync(ct);

        // Collapse every edition of the same title (same author) to ONE row so a
        // book that OpenLibrary lists as 17 separate works/editions doesn't appear
        // 17 times. Keep the EARLIEST record — earliest added (CreatedAt; nulls
        // count as "earliest", i.e. predating tracking), then earliest published —
        // so a title we already had never resurfaces as new in a later month.
        // Fall back to the raw title when NormalizedTitle is null so untitled-norm
        // rows still de-duplicate instead of each standing alone.
        return rows
            .GroupBy(r => $"{r.AuthorId}\0{r.NormalizedTitle ?? r.Title.Trim().ToLowerInvariant()}")
            .Select(g => g
                .OrderBy(r => r.CreatedAt ?? DateTime.MinValue)
                .ThenBy(r => r.FirstPublishYear ?? int.MaxValue)
                .First())
            .OrderByDescending(r => r.CreatedAt ?? DateTime.MinValue)
            .ThenByDescending(r => r.FirstPublishYear)
            .ThenBy(r => r.Title)
            .Select(r => new RecentReleaseRow(
                r.Id, r.Title, r.FirstPublishYear!.Value, r.CoverId,
                r.OpenLibraryWorkKey, r.AuthorId, r.AuthorName, r.AuthorPriority,
                r.Owned, r.ReadStatusStr, r.SeriesName, r.SeriesPosition, r.Subjects, r.CreatedAt))
            .ToList();
    }

    // Manually re-indexes the __unknown quarantine folder into the UnknownFiles
    // table (the same step sync runs as Phase 4). Returns diagnostics so it's
    // clear which roots were checked and how many files were seen — handy when
    // the table looks unexpectedly empty.
    [HttpPost("unknown-files/reindex")]
    public async Task<ActionResult<UnknownFileIndexer.RescanResult>> ReindexUnknownFiles(CancellationToken ct)
    {
        var locationPaths = await _db.LibraryLocations
            .AsNoTracking()
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);

        var result = await UnknownFileIndexer.RescanAsync(_db, locationPaths, ct);
        return Ok(result);
    }

    // -------------------------------------------------------------------------
    // File candidates & linking for missing works
    // -------------------------------------------------------------------------

    // Minimum fuzzy score (0–1) for a file to be offered as a match candidate.
    private const double FileCandidateMinScore = 0.5;

    public sealed record FileCandidateDto(
        // null when the candidate comes from the unknown folder (not yet in DB)
        int? FileId,
        string FullPath,
        string DisplayName,
        double Score,
        // "linked" = already an unmatched LocalBookFile; "unknown" = raw file
        string Source);

    // Returns every fuzzy-scored file candidate (score >= 50%) for a missing
    // book, best first. Purely DB-driven (no filesystem walk) — both sources are
    // merged into a single list ordered by score:
    //   1. Unmatched LocalBookFiles (no BookId) in ANY author folder
    //   2. __unknown quarantine files, indexed into the DB during sync
    [HttpGet("{id:int}/file-candidates")]
    public async Task<ActionResult<IReadOnlyList<FileCandidateDto>>> GetFileCandidates(
        int id, CancellationToken ct)
    {
        var book = await _db.Books
            .AsNoTracking()
            .Include(b => b.Author)
            .FirstOrDefaultAsync(b => b.Id == id, ct);
        if (book is null) return NotFound();

        var normalizedTarget = TitleNormalizer.Normalize(book.Title);

        // SQL-side pre-filter: the unmatched-file table can hold hundreds of
        // thousands of rows, far too many to pull into memory and fuzzy-score on
        // every request. Require the title's most distinctive (longest) word to
        // appear as a substring of the candidate's normalized title — a cheap
        // LIKE that slashes the row count before scoring. Titles with no word of
        // 4+ chars (very rare) fall back to scanning everything.
        var pivot = normalizedTarget
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4)
            .OrderByDescending(w => w.Length)
            .FirstOrDefault();
        var like = pivot is null ? null : $"%{pivot}%";

        var candidates = new List<FileCandidateDto>();

        // 1. Every unmatched LocalBookFile, regardless of which author folder it
        //    sits in — a file is often misfiled under the wrong author, so we
        //    don't restrict to this book's author. The author folder is shown
        //    in the display name when it differs, for context.
        var lbfQuery = _db.LocalBookFiles.AsNoTracking().Where(f => f.BookId == null);
        if (like is not null)
            lbfQuery = lbfQuery.Where(f => f.NormalizedTitle != null && EF.Functions.Like(f.NormalizedTitle, like));
        var unmatched = await lbfQuery
            .Select(f => new { f.Id, f.FullPath, f.MetadataTitle, f.NormalizedTitle, f.AuthorFolder, f.AuthorId })
            .ToListAsync(ct);

        foreach (var f in unmatched)
        {
            var raw = !string.IsNullOrWhiteSpace(f.MetadataTitle) ? f.MetadataTitle
                    : !string.IsNullOrWhiteSpace(f.NormalizedTitle) ? f.NormalizedTitle
                    : Path.GetFileNameWithoutExtension(f.FullPath);
            var score = FuzzyScore.JaroWinkler(normalizedTarget, TitleNormalizer.Normalize(raw));
            if (score < FileCandidateMinScore) continue;
            var fileName = Path.GetFileName(f.FullPath);
            var display = f.AuthorId != book.AuthorId && !string.IsNullOrWhiteSpace(f.AuthorFolder)
                ? $"{f.AuthorFolder} / {fileName}"
                : fileName;
            candidates.Add(new FileCandidateDto(f.Id, f.FullPath, display, score, "linked"));
        }

        // 2. __unknown quarantine files, indexed into the DB during sync — a
        //    plain DB read, no disk access on this request. Same pre-filter.
        var ufQuery = _db.UnknownFiles.AsNoTracking();
        if (like is not null)
            ufQuery = ufQuery.Where(u => u.NormalizedTitle != null && EF.Functions.Like(u.NormalizedTitle, like));
        var unknownFiles = await ufQuery
            .Select(u => new { u.FullPath, u.FileName, u.NormalizedTitle })
            .ToListAsync(ct);

        foreach (var u in unknownFiles)
        {
            var score = FuzzyScore.JaroWinkler(normalizedTarget, u.NormalizedTitle ?? "");
            if (score < FileCandidateMinScore) continue;
            candidates.Add(new FileCandidateDto(null, u.FullPath, u.FileName, score, "unknown"));
        }

        var ordered = candidates
            .OrderByDescending(c => c.Score)
            .ToList();

        return Ok(ordered);
    }

    public sealed record LinkFileRequest(
        // Provide one of the two:
        int? FileId,        // existing LocalBookFile to link
        string? FilePath,   // raw path from unknown folder
        bool Move           // if true, move the file into the author's library folder
    );

    public sealed record LinkFileResult(bool Moved, string FinalPath);

    // Links a file (existing LocalBookFile or raw unknown-folder file) to the
    // specified book, marking the book as owned. If Move=true and the file is
    // not already under a library location, it is moved into the author's folder.
    [HttpPost("{id:int}/link-file")]
    public async Task<ActionResult<LinkFileResult>> LinkFile(
        int id, [FromBody] LinkFileRequest body, CancellationToken ct)
    {
        var book = await _db.Books
            .Include(b => b.Author)
            .FirstOrDefaultAsync(b => b.Id == id, ct);
        if (book is null) return NotFound(new { error = "Book not found" });

        var locationPaths = await _db.LibraryLocations
            .AsNoTracking()
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);

        string sourcePath;
        LocalBookFile? existingFile = null;

        if (body.FileId is int fid)
        {
            existingFile = await _db.LocalBookFiles.FirstOrDefaultAsync(f => f.Id == fid, ct);
            if (existingFile is null) return NotFound(new { error = "File not found" });
            sourcePath = existingFile.FullPath;
        }
        else if (!string.IsNullOrWhiteSpace(body.FilePath))
        {
            sourcePath = body.FilePath;
            if (!System.IO.File.Exists(sourcePath))
                return BadRequest(new { error = "File does not exist on disk" });
        }
        else
        {
            return BadRequest(new { error = "Provide either FileId or FilePath" });
        }

        var finalPath = sourcePath;
        var moved = false;

        if (body.Move)
        {
            // Determine the author's canonical folder under the primary location
            var primaryLocation = await _db.LibraryLocations
                .AsNoTracking()
                .Where(l => l.Enabled && l.IsPrimary)
                .OrderBy(l => l.Id)
                .FirstOrDefaultAsync(ct)
                ?? await _db.LibraryLocations
                    .AsNoTracking()
                    .Where(l => l.Enabled)
                    .OrderBy(l => l.Id)
                    .FirstOrDefaultAsync(ct);

            if (primaryLocation is not null)
            {
                var authorFolder = book.Author?.Name is string n
                    ? Path.Combine(primaryLocation.Path, n)
                    : primaryLocation.Path;
                var destDir = Path.Combine(authorFolder, TitleNormalizer.Normalize(book.Title));
                if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
                var dest = Path.Combine(destDir, Path.GetFileName(sourcePath));
                if (!dest.Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    _fs.MoveFile(sourcePath, dest, overwrite: false);
                    finalPath = dest;
                    moved = true;
                }
            }
        }

        if (existingFile is not null)
        {
            existingFile.BookId = book.Id;
            // Re-home the file to the target book's author — the candidate may
            // have been an unmatched file sitting under a different author.
            existingFile.AuthorId = book.AuthorId;
            existingFile.ManuallyUnmatched = false;
            if (moved)
            {
                existingFile.FullPath = finalPath;
                existingFile.AuthorFolder = book.Author?.Name ?? existingFile.AuthorFolder;
            }
        }
        else
        {
            // Create a new LocalBookFile record for the formerly-unknown file
            var newFile = new LocalBookFile
            {
                AuthorId = book.AuthorId,
                BookId = book.Id,
                FullPath = finalPath,
                AuthorFolder = book.Author?.Name ?? "",
                TitleFolder = Path.GetDirectoryName(finalPath) ?? "",
                NormalizedTitle = TitleNormalizer.Normalize(book.Title),
                ModifiedAt = System.IO.File.GetLastWriteTimeUtc(finalPath),
                SizeBytes = new FileInfo(finalPath).Length,
            };
            _db.LocalBookFiles.Add(newFile);

            // This file came out of the __unknown quarantine — drop its index
            // row so it stops showing up as a candidate (the next sync would
            // also prune it, but do it now for immediacy).
            var indexRows = await _db.UnknownFiles
                .Where(u => u.FullPath == sourcePath)
                .ToListAsync(ct);
            if (indexRows.Count > 0) _db.UnknownFiles.RemoveRange(indexRows);
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new LinkFileResult(moved, finalPath));
    }
}

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.Import;
using TheLibrary.Server.Services.Incoming;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ImportController : ControllerBase
{
    private readonly LibraryDbContext _db;
    private readonly ManualBookService _manualBooks;
    private readonly OpenLibraryClient _openLibrary;

    public ImportController(LibraryDbContext db, ManualBookService manualBooks, OpenLibraryClient openLibrary)
    {
        _db = db;
        _manualBooks = manualBooks;
        _openLibrary = openLibrary;
    }

    public sealed record GoodreadsImportResult(
        int Matched,
        int AlreadyRead,
        int Unmatched,
        IReadOnlyList<string> UnmatchedTitles);

    // Accepts a Goodreads export CSV. Matches rows on normalized title + author
    // and sets ReadStatus = Read (or Reading / Unread for other shelves) and
    // ReadAt from the "Date Read" column. Rows with Exclusive Shelf = "to-read"
    // set Wanted = true on missing books.
    [HttpPost("goodreads")]
    [RequestSizeLimit(10 * 1024 * 1024)] // 10 MB
    public async Task<ActionResult<GoodreadsImportResult>> Goodreads(
        IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "No file uploaded" });

        List<GoodreadsRow> rows;
        try
        {
            using var reader = new StreamReader(file.OpenReadStream());
            rows = ParseGoodreadsCsv(await reader.ReadToEndAsync(ct));
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = $"Failed to parse CSV: {ex.Message}" });
        }

        // Load all books with their authors into memory for matching.
        var allBooks = await _db.Books.AsNoTracking()
            .Select(b => new { b.Id, b.NormalizedTitle, AuthorNorm = b.Author.Name })
            .ToListAsync(ct);

        // Build lookup: normalized-title -> list of book ids
        var byTitle = allBooks
            .Where(b => !string.IsNullOrEmpty(b.NormalizedTitle))
            .GroupBy(b => b.NormalizedTitle!)
            .ToDictionary(g => g.Key, g => g.Select(b => b.Id).ToList());

        int matched = 0, alreadyRead = 0, unmatched = 0;
        var unmatchedTitles = new List<string>();
        var toUpdate = new Dictionary<int, (ReadStatus Status, DateTime? ReadAt, bool Wanted)>();

        foreach (var row in rows)
        {
            var normTitle = TitleNormalizer.Normalize(row.Title);
            if (string.IsNullOrEmpty(normTitle)) continue;

            if (!byTitle.TryGetValue(normTitle, out var candidateIds))
            {
                unmatched++;
                unmatchedTitles.Add(row.Title);
                continue;
            }

            // If multiple books share the same normalized title, pick the best
            // match by also checking author (normalized). Fall back to first.
            var normAuthor = TitleNormalizer.NormalizeAuthor(row.Author);
            var allCandidates = allBooks.Where(b => candidateIds.Contains(b.Id)).ToList();
            var best = allCandidates.FirstOrDefault(b =>
                TitleNormalizer.NormalizeAuthor(b.AuthorNorm) == normAuthor)
                ?? allCandidates[0];

            var readStatus = row.Shelf switch
            {
                "read" => ReadStatus.Read,
                "currently-reading" => ReadStatus.Reading,
                _ => ReadStatus.Unread
            };

            if (toUpdate.TryGetValue(best.Id, out var existing) && existing.Status == ReadStatus.Read)
            {
                alreadyRead++;
                continue;
            }

            var wanted = row.Shelf == "to-read";
            toUpdate[best.Id] = (readStatus, row.DateRead, wanted);
            matched++;
        }

        // Batch update in chunks to avoid huge SQL statements.
        var readIds = toUpdate.Where(kv => kv.Value.Status == ReadStatus.Read).Select(kv => kv.Key).ToList();
        var readingIds = toUpdate.Where(kv => kv.Value.Status == ReadStatus.Reading).Select(kv => kv.Key).ToList();
        var wantedIds = toUpdate.Where(kv => kv.Value.Wanted).Select(kv => kv.Key).ToList();

        // For "read" rows we need to set ReadAt per-row — do individually for those with a date.
        var readWithDate = toUpdate.Where(kv => kv.Value.Status == ReadStatus.Read && kv.Value.ReadAt.HasValue).ToList();
        var readWithoutDate = readIds.Except(readWithDate.Select(kv => kv.Key)).ToList();

        if (readWithoutDate.Count > 0)
        {
            await _db.Books.Where(b => readWithoutDate.Contains(b.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.ReadStatus, _ => ReadStatus.Read)
                    .SetProperty(b => b.ReadAt, _ => (DateTime?)null), ct);
        }

        foreach (var (id, (_, readAt, _)) in readWithDate)
        {
            var book = await _db.Books.FirstOrDefaultAsync(b => b.Id == id, ct);
            if (book is null) continue;
            book.ReadStatus = ReadStatus.Read;
            book.ReadAt = readAt;
        }

        if (readingIds.Count > 0)
            await _db.Books.Where(b => readingIds.Contains(b.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.ReadStatus, _ => ReadStatus.Reading), ct);

        if (wantedIds.Count > 0)
            await _db.Books.Where(b => wantedIds.Contains(b.Id) && !b.ManuallyOwned && !b.OwnedDifferentEdition && !b.LocalFiles.Any())
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.Wanted, _ => true), ct);

        await _db.SaveChangesAsync(ct);

        return Ok(new GoodreadsImportResult(
            matched, alreadyRead, unmatched,
            unmatchedTitles.Take(50).ToList()));
    }

    public sealed record PhysicalBooksImportResult(
        int Matched,
        int AlreadyOwned,
        int Skipped,
        IReadOnlyList<string> Unmatched);

    // Accepts a fixed-width plain-text file with three columns:
    //   Author (26 chars)  Title (44 chars)  Series+position (rest)
    // Also accepts tab-separated rows. Rows with an empty title are skipped.
    // Matching is by normalised title + author; books that match are marked
    // ManuallyOwned = true. Already-owned books are counted but not updated.
    [HttpPost("physical-books")]
    [RequestSizeLimit(5 * 1024 * 1024)]
    public async Task<ActionResult<PhysicalBooksImportResult>> PhysicalBooks(
        IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "No file uploaded" });

        string text;
        try
        {
            using var reader = new StreamReader(file.OpenReadStream());
            text = await reader.ReadToEndAsync(ct);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = $"Failed to read file: {ex.Message}" });
        }

        var rows = ParsePhysicalBooksFile(text);
        var index = await PhysicalMatchIndex.LoadAsync(_db, ct);

        int matched = 0, alreadyOwned = 0, skipped = 0;
        var unmatchedRows = new List<PhysicalBookRow>();
        var toMarkOwned = new HashSet<int>();
        var seen = new HashSet<int>();

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Title)) { skipped++; continue; }

            var hit = index.TryMatch(row.Title, row.Author, row.Isbn);
            if (hit is null) { unmatchedRows.Add(row); continue; }

            if (!seen.Add(hit.Value.Id)) continue;

            if (hit.Value.ManuallyOwned) { alreadyOwned++; continue; }

            toMarkOwned.Add(hit.Value.Id);
            matched++;
        }

        if (toMarkOwned.Count > 0)
            await MarkOwnedAsync(toMarkOwned, ct);

        await PersistUnmatchedAsync(unmatchedRows, ct);

        // Display string for each unmatched row — built once from the source row.
        var unmatchedDisplay = unmatchedRows
            .Select(FormatUnmatched)
            .ToList();

        return Ok(new PhysicalBooksImportResult(
            matched, alreadyOwned, skipped, unmatchedDisplay));
    }

    // GET /api/import/physical-books/unmatched
    [HttpGet("physical-books/unmatched")]
    public async Task<IActionResult> GetUnmatched(CancellationToken ct)
    {
        var rows = await _db.PhysicalBookUnmatched
            .OrderBy(u => u.Author).ThenBy(u => u.Title)
            .Select(u => new { u.Id, u.Author, u.Title, u.SeriesPos, u.Isbn, u.AddedAt })
            .ToListAsync(ct);
        return Ok(rows);
    }

    public sealed record UpdateUnmatchedRequest(string Author, string Title, string SeriesPos, string? Isbn);

    // PUT /api/import/physical-books/unmatched/{id}
    [HttpPut("physical-books/unmatched/{id:int}")]
    public async Task<IActionResult> UpdateUnmatched(int id, [FromBody] UpdateUnmatchedRequest req, CancellationToken ct)
    {
        var row = await _db.PhysicalBookUnmatched.FindAsync([id], ct);
        if (row is null) return NotFound();
        row.Author    = req.Author ?? "";
        row.Title     = req.Title ?? "";
        row.SeriesPos = req.SeriesPos ?? "";
        // Normalise the ISBN so a hyphenated/spaced entry still matches.
        row.Isbn      = string.IsNullOrWhiteSpace(req.Isbn) ? null : EpubMetadataReader.NormaliseIsbn(req.Isbn);
        await _db.SaveChangesAsync(ct);
        return Ok(new { row.Id, row.Author, row.Title, row.SeriesPos, row.Isbn, row.AddedAt });
    }

    // DELETE /api/import/physical-books/unmatched/{id}
    [HttpDelete("physical-books/unmatched/{id:int}")]
    public async Task<IActionResult> DeleteUnmatched(int id, CancellationToken ct)
    {
        await _db.PhysicalBookUnmatched.Where(u => u.Id == id).ExecuteDeleteAsync(ct);
        return NoContent();
    }

    public sealed record RematchResult(int Matched, int StillUnmatched);

    // Title Jaro-Winkler score at or above which the rematch auto-accepts a
    // fuzzy hit (the exact and loose lookups having both missed). The author
    // must independently match too — see PhysicalMatchIndex.TryFuzzyMatch.
    private const double FuzzyRematchTitleThreshold = 0.9;

    // POST /api/import/physical-books/unmatched/rematch
    // Re-runs matching over every unmatched row. Each row is tried against the
    // exact + loose-key lookup first; rows that still miss fall back to a
    // fuzzy title match (scoped to a matching author) so near-identical titles
    // get picked up without the user resolving them by hand.
    [HttpPost("physical-books/unmatched/rematch")]
    public async Task<ActionResult<RematchResult>> Rematch(CancellationToken ct)
    {
        var pending = await _db.PhysicalBookUnmatched.ToListAsync(ct);
        if (pending.Count == 0) return Ok(new RematchResult(0, 0));

        var index = await PhysicalMatchIndex.LoadAsync(_db, ct);

        var toMarkOwned = new HashSet<int>();
        var nowMatched = new List<int>();

        foreach (var row in pending)
        {
            var hit = index.TryMatch(row.Title, row.Author, row.Isbn)
                      ?? index.TryFuzzyMatch(row.Title, row.Author, FuzzyRematchTitleThreshold);
            if (hit is null) continue;
            if (!hit.Value.ManuallyOwned)
                toMarkOwned.Add(hit.Value.Id);
            nowMatched.Add(row.Id);
        }

        await ApplyResolutionAsync(toMarkOwned, nowMatched, ct);

        return Ok(new RematchResult(nowMatched.Count, pending.Count - nowMatched.Count));
    }

    // ── Resolving unmatched physical rows ────────────────────────────────────

    public sealed record AuthorCandidate(int AuthorId, string Name, double Score);
    public sealed record RowAuthorSuggestions(int Id, IReadOnlyList<AuthorCandidate> Candidates);

    // Author candidates for every unmatched physical row. An exact name — in
    // either "Forename Surname" or the "Surname, Forename" sort order —
    // resolves by dictionary lookup and comes back as a single 1.0 candidate
    // the UI auto-selects; only rows with no exact hit fall back to fuzzy
    // scoring. Only top-level authors and pen names are offered — a
    // non-pen-name child's books fold into its canonical, so cataloguing a
    // physical book against one would hide it from the library view.
    [HttpGet("physical-books/unmatched/author-suggestions")]
    public async Task<ActionResult<IReadOnlyList<RowAuthorSuggestions>>> AuthorSuggestions(
        CancellationToken ct)
    {
        var rows = await _db.PhysicalBookUnmatched
            .Select(u => new { u.Id, u.Author })
            .ToListAsync(ct);
        if (rows.Count == 0) return Ok(Array.Empty<RowAuthorSuggestions>());

        var authors = await _db.Authors
            .Where(a => a.LinkedToAuthorId == null || a.IsPenName)
            .Select(a => new { a.Id, a.Name })
            .ToListAsync(ct);

        // Bucket authors by every order-variant of their normalized name so an
        // exact name in any order resolves with a dictionary lookup. The
        // per-author variant list is kept too, for the fuzzy fallback.
        var byVariant = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var authorVariants = new List<(int Id, string Name, List<string> Variants)>(authors.Count);
        foreach (var a in authors)
        {
            var variants = AuthorMatcher
                .ExpandNameVariants(TitleNormalizer.NormalizeAuthor(a.Name))
                .ToList();
            authorVariants.Add((a.Id, a.Name, variants));
            foreach (var v in variants)
            {
                if (!byVariant.TryGetValue(v, out var ids))
                    byVariant[v] = ids = new List<int>();
                if (!ids.Contains(a.Id)) ids.Add(a.Id);
            }
        }
        var nameById = authors.ToDictionary(a => a.Id, a => a.Name);

        var result = new List<RowAuthorSuggestions>(rows.Count);
        foreach (var row in rows)
        {
            var rowVariants = AuthorMatcher
                .ExpandNameVariants(TitleNormalizer.NormalizeAuthor(row.Author))
                .ToList();

            // 1. Exact name match, order-insensitive — the common case for an
            //    inventory whose authors are real names or "Surname, Forename".
            var exact = new List<int>();
            foreach (var v in rowVariants)
                if (byVariant.TryGetValue(v, out var ids))
                    foreach (var id in ids)
                        if (!exact.Contains(id)) exact.Add(id);

            List<AuthorCandidate> candidates;
            if (exact.Count > 0)
            {
                candidates = exact
                    .Select(id => new AuthorCandidate(id, nameById[id], 1.0))
                    .ToList();
            }
            else
            {
                // 2. No exact hit — fuzzy-score so a typo or a middle-initial
                //    difference still surfaces a usable suggestion.
                candidates = authorVariants
                    .Select(a => new AuthorCandidate(a.Id, a.Name, BestNameScore(rowVariants, a.Variants)))
                    .Where(c => c.Score >= 0.4)
                    .OrderByDescending(c => c.Score)
                    .Take(3)
                    .ToList();
            }
            result.Add(new RowAuthorSuggestions(row.Id, candidates));
        }
        return Ok(result);

        // Best Jaro-Winkler score across the cross-product of name-order
        // variants — handles "First Last" vs "Last, First" vs "Last First".
        static double BestNameScore(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            double best = 0;
            foreach (var x in a)
                foreach (var y in b)
                {
                    var s = FuzzyScore.JaroWinkler(x, y);
                    if (s > best) best = s;
                }
            return best;
        }
    }

    public sealed record BookCandidate(
        int BookId, string Title, double Score,
        string? Series, string? SeriesPosition, bool AlreadyOwned);

    // Every book belonging to `authorId` (and its folded-in non-pen-name
    // children) scored against the unmatched row's title, best match first —
    // the same Jaro-Winkler ranking the author page uses for local files.
    [HttpGet("physical-books/unmatched/{id:int}/book-suggestions")]
    public async Task<ActionResult<IReadOnlyList<BookCandidate>>> BookSuggestions(
        int id, [FromQuery] int authorId, CancellationToken ct)
    {
        var row = await _db.PhysicalBookUnmatched.FindAsync([id], ct);
        if (row is null) return NotFound(new { error = "Unmatched row not found" });

        var foldedIds = new List<int> { authorId };
        foldedIds.AddRange(await _db.Authors
            .Where(a => a.LinkedToAuthorId == authorId && !a.IsPenName)
            .Select(a => a.Id)
            .ToListAsync(ct));

        var books = await _db.Books
            .Where(b => foldedIds.Contains(b.AuthorId))
            .Select(b => new
            {
                b.Id,
                b.Title,
                SeriesName = b.Series != null ? b.Series.Name : null,
                b.SeriesPosition,
                Owned = b.ManuallyOwned || b.OwnedDifferentEdition || b.LocalFiles.Any(),
            })
            .ToListAsync(ct);

        // Recompute the title key from Title — same robustness reasoning as
        // PhysicalMatchIndex: a stale/null stored NormalizedTitle would
        // wrongly score an exact title near zero.
        var normTitle = TitleNormalizer.Normalize(row.Title);
        var ranked = books
            .Select(b => new BookCandidate(
                b.Id, b.Title,
                FuzzyScore.JaroWinkler(TitleNormalizer.Normalize(b.Title), normTitle),
                b.SeriesName, b.SeriesPosition, b.Owned))
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Ok(ranked);
    }

    public sealed record MatchUnmatchedRequest(int BookId);

    // Resolve an unmatched physical row by tying it to an existing book: the
    // book is flagged ManuallyOwned (a physical copy is on the shelf) and the
    // unmatched row is cleared.
    [HttpPost("physical-books/unmatched/{id:int}/match")]
    public async Task<IActionResult> MatchUnmatched(
        int id, [FromBody] MatchUnmatchedRequest body, CancellationToken ct)
    {
        var row = await _db.PhysicalBookUnmatched.FindAsync([id], ct);
        if (row is null) return NotFound(new { error = "Unmatched row not found" });

        var book = await _db.Books.FirstOrDefaultAsync(b => b.Id == body.BookId, ct);
        if (book is null) return NotFound(new { error = "Book not found" });

        if (!book.ManuallyOwned)
        {
            book.ManuallyOwned = true;
            book.ManuallyOwnedAt = DateTime.UtcNow;
        }
        _db.PhysicalBookUnmatched.Remove(row);
        await _db.SaveChangesAsync(ct);
        return Ok(new { matchedBookId = book.Id, book.Title });
    }

    public sealed record AddUnmatchedBookRequest(
        int AuthorId, string? Title, string? SeriesName, string? SeriesPosition);

    public sealed record OpenLibraryWorkCandidate(
        string Key, string Title, int? FirstPublishYear, int? CoverId, string? Authors,
        string? PrimaryAuthorKey, string? PrimaryAuthorName);

    public sealed record AddUnmatchedOpenLibraryBookRequest(
        int AuthorId,
        string WorkKey,
        string? Title,
        int? FirstPublishYear,
        int? CoverId,
        string? Authors);

    // Resolve an unmatched physical row by creating a brand-new book for the
    // chosen author. The book is owned (a physical copy is on the shelf) and
    // gets a synthetic "XX" work key so a later OL refresh can promote it.
    [HttpPost("physical-books/unmatched/{id:int}/add-book")]
    public async Task<IActionResult> AddUnmatchedBook(
        int id, [FromBody] AddUnmatchedBookRequest body, CancellationToken ct)
    {
        var row = await _db.PhysicalBookUnmatched.FindAsync([id], ct);
        if (row is null) return NotFound(new { error = "Unmatched row not found" });

        var title = string.IsNullOrWhiteSpace(body.Title) ? row.Title : body.Title;
        var result = await _manualBooks.CreateAsync(
            body.AuthorId, title, firstPublishYear: null,
            body.SeriesName, body.SeriesPosition, owned: true, ct);

        if (result.Error is not null)
            return result.Conflict
                ? Conflict(new { error = result.Error })
                : BadRequest(new { error = result.Error });

        _db.PhysicalBookUnmatched.Remove(row);
        await _db.SaveChangesAsync(ct);
        return Ok(new { createdBookId = result.Book!.Id, result.Book.Title });
    }

    [HttpGet("physical-books/unmatched/{id:int}/openlibrary-search")]
    public async Task<ActionResult<IReadOnlyList<OpenLibraryWorkCandidate>>> SearchOpenLibraryWorks(
        int id,
        [FromQuery] string? title,
        CancellationToken ct)
    {
        var row = await _db.PhysicalBookUnmatched.FindAsync([id], ct);
        if (row is null) return NotFound(new { error = "Unmatched row not found" });

        var queryTitle = string.IsNullOrWhiteSpace(title) ? row.Title : title.Trim();
        if (string.IsNullOrWhiteSpace(queryTitle)) return Ok(Array.Empty<OpenLibraryWorkCandidate>());

        WorkSearchResponse? resp;
        try
        {
            // For the unmatched physical-book flow the OpenLibrary lookup is
            // used to identify the right work and author, so it must never be
            // narrowed by the possibly-wrong imported author string.
            resp = await _openLibrary.SearchWorksAsync(queryTitle, author: null, ct);
        }
        catch (OpenLibraryRequestFailedException ex)
        {
            return Problem(
                title: "OpenLibrary request failed",
                detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var results = resp?.Docs?
            .Where(d => !string.IsNullOrWhiteSpace(d.Key) && !string.IsNullOrWhiteSpace(d.Title))
            .Select(d => new OpenLibraryWorkCandidate(
                d.Key!,
                d.Title!,
                d.FirstPublishYear,
                d.CoverId,
                d.AuthorNames is { Count: > 0 } ? string.Join(", ", d.AuthorNames.Where(n => !string.IsNullOrWhiteSpace(n))) : null,
                d.AuthorKeys?.FirstOrDefault(k => !string.IsNullOrWhiteSpace(k)),
                d.AuthorNames?.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))))
            .ToList()
            ?? new List<OpenLibraryWorkCandidate>();

        return Ok(results);
    }

    [HttpPost("physical-books/unmatched/{id:int}/add-openlibrary-book")]
    public async Task<IActionResult> AddUnmatchedOpenLibraryBook(
        int id,
        [FromBody] AddUnmatchedOpenLibraryBookRequest body,
        CancellationToken ct)
    {
        var row = await _db.PhysicalBookUnmatched.FindAsync([id], ct);
        if (row is null) return NotFound(new { error = "Unmatched row not found" });

        var author = await _db.Authors.FirstOrDefaultAsync(a => a.Id == body.AuthorId, ct);
        if (author is null) return BadRequest(new { error = "Author not found" });

        var workKey = body.WorkKey?.Trim();
        if (string.IsNullOrWhiteSpace(workKey))
            return BadRequest(new { error = "OpenLibrary work key is required" });
        if (workKey.StartsWith("/works/", StringComparison.OrdinalIgnoreCase))
            workKey = workKey[("/works/".Length)..];

        var existing = await _db.Books.FirstOrDefaultAsync(
            b => b.AuthorId == body.AuthorId && b.OpenLibraryWorkKey == workKey,
            ct);
        if (existing is not null)
        {
            if (!existing.ManuallyOwned)
            {
                existing.ManuallyOwned = true;
                existing.ManuallyOwnedAt = DateTime.UtcNow;
            }
            _db.PhysicalBookUnmatched.Remove(row);
            await _db.SaveChangesAsync(ct);
            return Ok(new { createdBookId = existing.Id, existing.Title, existing.OpenLibraryWorkKey });
        }

        var cleanTitle = string.IsNullOrWhiteSpace(body.Title) ? row.Title.Trim() : body.Title.Trim();
        if (string.IsNullOrWhiteSpace(cleanTitle))
            return BadRequest(new { error = "Title is required" });

        var book = new Book
        {
            AuthorId = body.AuthorId,
            OpenLibraryWorkKey = workKey,
            Title = cleanTitle,
            NormalizedTitle = TitleNormalizer.Normalize(cleanTitle),
            FirstPublishYear = body.FirstPublishYear,
            CoverId = body.CoverId,
            ManuallyOwned = true,
            ManuallyOwnedAt = DateTime.UtcNow,
            Subjects = "",
            Isbn = row.Isbn,
            // Past publish year → dated to 1 Jan of that year (not "now") so it
            // groups under its real release period, not the current month.
            CreatedAt = Book.CreatedAtForPublishYear(body.FirstPublishYear),
        };

        _db.Books.Add(book);
        _db.PhysicalBookUnmatched.Remove(row);
        await _db.SaveChangesAsync(ct);
        return Ok(new { createdBookId = book.Id, book.Title, book.OpenLibraryWorkKey });
    }

    public sealed record RematchProposal(
        int UnmatchedId, string UnmatchedAuthor, string UnmatchedTitle,
        int BookId, string BookTitle, string BookAuthor, bool BookAlreadyOwned);

    // Dry run of the rematch: returns the book each unmatched row WOULD be
    // tied to, without changing anything — feeds the review-before-apply UI.
    [HttpPost("physical-books/unmatched/rematch/preview")]
    public async Task<ActionResult<IReadOnlyList<RematchProposal>>> RematchPreview(CancellationToken ct)
    {
        var pending = await _db.PhysicalBookUnmatched.AsNoTracking().ToListAsync(ct);
        if (pending.Count == 0) return Ok(Array.Empty<RematchProposal>());

        var index = await PhysicalMatchIndex.LoadAsync(_db, ct);

        var hits = new List<(int RowId, string RowAuthor, string RowTitle, int BookId)>();
        foreach (var row in pending)
        {
            var hit = index.TryMatch(row.Title, row.Author, row.Isbn)
                      ?? index.TryFuzzyMatch(row.Title, row.Author, FuzzyRematchTitleThreshold);
            if (hit is not null) hits.Add((row.Id, row.Author, row.Title, hit.Value.Id));
        }
        if (hits.Count == 0) return Ok(Array.Empty<RematchProposal>());

        var bookIds = hits.Select(h => h.BookId).Distinct().ToList();
        var books = await _db.Books.AsNoTracking()
            .Where(b => bookIds.Contains(b.Id))
            .Select(b => new
            {
                b.Id,
                b.Title,
                AuthorName = b.Author.Name,
                Owned = b.ManuallyOwned || b.OwnedDifferentEdition || b.LocalFiles.Any(),
            })
            .ToDictionaryAsync(b => b.Id, ct);

        return Ok(hits
            .Where(h => books.ContainsKey(h.BookId))
            .Select(h =>
            {
                var bk = books[h.BookId];
                return new RematchProposal(
                    h.RowId, h.RowAuthor, h.RowTitle,
                    bk.Id, bk.Title, bk.AuthorName, bk.Owned);
            })
            .ToList());
    }

    public sealed record BulkResolveItem(int UnmatchedId, int BookId);
    public sealed record BulkResolveRequest(IReadOnlyList<BulkResolveItem> Items);
    public sealed record BulkResolveResult(int Resolved);

    // Applies a reviewed set of (unmatched row → book) matches: each book is
    // flagged owned and its row cleared, all in one transaction.
    [HttpPost("physical-books/unmatched/bulk-resolve")]
    public async Task<ActionResult<BulkResolveResult>> BulkResolve(
        [FromBody] BulkResolveRequest body, CancellationToken ct)
    {
        var items = (body.Items ?? Array.Empty<BulkResolveItem>())
            .Where(i => i.UnmatchedId > 0 && i.BookId > 0)
            .ToList();
        if (items.Count == 0) return Ok(new BulkResolveResult(0));

        var unmatchedIds = items.Select(i => i.UnmatchedId).Distinct().ToList();
        var bookIds = items.Select(i => i.BookId).Distinct().ToList();

        var validRows = (await _db.PhysicalBookUnmatched
            .Where(u => unmatchedIds.Contains(u.Id))
            .Select(u => u.Id)
            .ToListAsync(ct)).ToHashSet();
        var booksToOwn = await _db.Books
            .Where(b => bookIds.Contains(b.Id) && !b.ManuallyOwned)
            .Select(b => b.Id)
            .ToListAsync(ct);

        var rowsToClear = items
            .Where(i => validRows.Contains(i.UnmatchedId))
            .Select(i => i.UnmatchedId)
            .Distinct()
            .ToList();
        if (rowsToClear.Count == 0) return Ok(new BulkResolveResult(0));

        await ApplyResolutionAsync(booksToOwn, rowsToClear, ct);
        return Ok(new BulkResolveResult(rowsToClear.Count));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // Marks a set of books as ManuallyOwned = true with the current timestamp.
    private Task MarkOwnedAsync(IEnumerable<int> bookIds, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var ids = bookIds.ToList();
        return _db.Books
            .Where(b => ids.Contains(b.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.ManuallyOwned, _ => true)
                .SetProperty(b => b.ManuallyOwnedAt, _ => now), ct);
    }

    // Marks the given books owned and clears the given unmatched rows as one
    // atomic unit, so a crash can't leave a book owned with its row still
    // present (or vice versa). Wrapped in an execution strategy because the
    // connection is configured with retry-on-failure.
    private async Task ApplyResolutionAsync(
        IReadOnlyCollection<int> bookIdsToOwn,
        IReadOnlyCollection<int> unmatchedIdsToClear,
        CancellationToken ct)
    {
        if (bookIdsToOwn.Count == 0 && unmatchedIdsToClear.Count == 0) return;
        var clearIds = unmatchedIdsToClear.ToList();
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            if (bookIdsToOwn.Count > 0)
                await MarkOwnedAsync(bookIdsToOwn, ct);
            if (clearIds.Count > 0)
                await _db.PhysicalBookUnmatched
                    .Where(u => clearIds.Contains(u.Id))
                    .ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
        });
    }

    // Adds the given rows to PhysicalBookUnmatched, skipping any row whose
    // (author, title) already exists case-insensitively.
    private async Task PersistUnmatchedAsync(IReadOnlyList<PhysicalBookRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        var existingKeys = await _db.PhysicalBookUnmatched
            .Select(u => new { u.Author, u.Title })
            .ToListAsync(ct);
        var keys = existingKeys
            .Select(u => (u.Author.Trim().ToLowerInvariant(), u.Title.Trim().ToLowerInvariant()))
            .ToHashSet();

        var addedAt = DateTime.UtcNow;
        foreach (var row in rows)
        {
            var key = (row.Author.Trim().ToLowerInvariant(), row.Title.Trim().ToLowerInvariant());
            if (!keys.Add(key)) continue;

            _db.PhysicalBookUnmatched.Add(new Data.Models.PhysicalBookUnmatched
            {
                Author    = row.Author,
                Title     = row.Title,
                SeriesPos = row.SeriesPos,
                Isbn      = row.Isbn,
                AddedAt   = addedAt,
            });
        }
        await _db.SaveChangesAsync(ct);
    }

    private static string FormatUnmatched(PhysicalBookRow row) =>
        string.IsNullOrWhiteSpace(row.Author) ? row.Title : $"{row.Author} — {row.Title}";

    private sealed record PhysicalBookRow(string Author, string Title, string SeriesPos, string? Isbn);

    // PhysicalMatchIndex now lives in Services/Import/PhysicalMatchIndex.cs so
    // it can be unit-tested directly without spinning up a DbContext.

    // Matches a contiguous ISBN-shaped token (digits with optional hyphens,
    // optional trailing X). Each hit is validated by NormaliseIsbn.
    private static readonly Regex IsbnTokenRx =
        new(@"[0-9][0-9-]{8,16}[0-9Xx]", RegexOptions.Compiled);

    // Pulls a valid ISBN off an import line: a dedicated tab column wins,
    // otherwise any ISBN-shaped token anywhere on the line is tried.
    private static string? ExtractIsbn(string line, string? tabColumn)
    {
        if (!string.IsNullOrWhiteSpace(tabColumn))
        {
            var fromColumn = EpubMetadataReader.NormaliseIsbn(tabColumn);
            if (fromColumn is not null) return fromColumn;
        }
        foreach (Match m in IsbnTokenRx.Matches(line))
        {
            var isbn = EpubMetadataReader.NormaliseIsbn(m.Value);
            if (isbn is not null) return isbn;
        }
        return null;
    }

    // Parses the fixed-width layout: author (26 chars), title (44 chars), series (rest).
    // Falls back to tab-separated columns when the line contains a tab character.
    // An ISBN — a tab-separated 4th column, or any ISBN-shaped token on the
    // line — is captured when present and used as the strongest match key.
    private static List<PhysicalBookRow> ParsePhysicalBooksFile(string text)
    {
        var rows = new List<PhysicalBookRow>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;

            string author, title, seriesPos;
            string? isbnColumn = null;
            if (line.Contains('\t'))
            {
                var parts = line.Split('\t');
                author    = parts.Length > 0 ? parts[0].Trim() : "";
                title     = parts.Length > 1 ? parts[1].Trim() : "";
                seriesPos = parts.Length > 2 ? parts[2].Trim() : "";
                isbnColumn = parts.Length > 3 ? parts[3].Trim() : null;
            }
            else
            {
                var padded = line.TrimEnd();
                if (padded.Length >= 70)
                {
                    author    = padded[..26].Trim();
                    title     = padded[26..70].Trim();
                    seriesPos = padded[70..].Trim();
                }
                else if (padded.Length >= 26)
                {
                    author    = padded[..26].Trim();
                    title     = padded[26..].Trim();
                    seriesPos = "";
                }
                else
                {
                    author    = padded.Trim();
                    title     = "";
                    seriesPos = "";
                }
            }

            if (string.IsNullOrWhiteSpace(author)) continue;
            rows.Add(new PhysicalBookRow(author, title, seriesPos, ExtractIsbn(line, isbnColumn)));
        }
        return rows;
    }

    private sealed record GoodreadsRow(
        string Title, string Author, string Shelf, DateTime? DateRead, int? Rating);

    private static List<GoodreadsRow> ParseGoodreadsCsv(string csv)
    {
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return new();

        // Parse header to find column indices robustly.
        var header = SplitCsvLine(lines[0]);
        int Idx(string name) => Array.FindIndex(header, h => h.Equals(name, StringComparison.OrdinalIgnoreCase));

        var titleIdx = Idx("Title");
        var authorIdx = Idx("Author");
        var shelfIdx = Idx("Exclusive Shelf");
        var dateReadIdx = Idx("Date Read");
        var ratingIdx = Idx("My Rating");

        if (titleIdx < 0 || authorIdx < 0) return new();

        var rows = new List<GoodreadsRow>();
        for (var i = 1; i < lines.Length; i++)
        {
            var cols = SplitCsvLine(lines[i]);
            if (cols.Length <= Math.Max(titleIdx, authorIdx)) continue;

            var title = cols[titleIdx].Trim('"', ' ');
            var author = authorIdx < cols.Length ? cols[authorIdx].Trim('"', ' ') : "";
            var shelf = shelfIdx >= 0 && shelfIdx < cols.Length ? cols[shelfIdx].Trim('"', ' ') : "";
            var dateStr = dateReadIdx >= 0 && dateReadIdx < cols.Length ? cols[dateReadIdx].Trim('"', ' ') : "";
            var ratingStr = ratingIdx >= 0 && ratingIdx < cols.Length ? cols[ratingIdx].Trim('"', ' ') : "";

            DateTime? dateRead = DateTime.TryParse(dateStr, out var d) ? d : null;
            int? rating = int.TryParse(ratingStr, out var r) ? r : null;

            if (!string.IsNullOrWhiteSpace(title))
                rows.Add(new GoodreadsRow(title, author, shelf, dateRead, rating));
        }

        return rows;
    }

    // Minimal CSV line splitter that handles quoted fields (including commas inside quotes).
    private static string[] SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                { sb.Append('"'); i++; }
                else
                    inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }
            else
                sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields.ToArray();
    }
}

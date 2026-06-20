using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.IO;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Controllers;

// Review surface for the content-scan results (BookContentScan): the author /
// title / series guessed from each unmatched-or-untracked file's front matter,
// so the user can confirm whether the determination is correct before anything
// acts on it.
[ApiController]
[Route("api/identified")]
public class IdentifiedController : ControllerBase
{
    private readonly LibraryDbContext _db;

    public IdentifiedController(LibraryDbContext db) => _db = db;

    // Confidence floor for fuzzily linking a catalogue title to an owned book.
    // Both sides are already normalized (articles/punctuation stripped), so this
    // only needs to absorb small spelling/subtitle differences — kept high to
    // avoid wrongly pulling an unrelated book into a series.
    private const double CatalogFuzzyFloor = 0.88;

    public sealed record SeriesListingRow(string Series, string? Genre, IReadOnlyList<string> Titles);

    public sealed record IdentifiedRow(
        int Id,
        int? FileId,            // LocalBookFile id when the path is a tracked file (for preview)
        string Path,
        string? Format,
        string Source,
        int? AuthorId,
        string? LinkedAuthorName,
        string? Isbn,
        string? Title,
        string? Author,
        string? Series,
        string? SeriesPosition,
        IReadOnlyList<string> AlsoBy,
        IReadOnlyList<SeriesListingRow> SeriesCatalog,
        DateTime ScannedAt);

    /// <summary>
    /// Unreviewed content-scan guesses that found something, newest first.
    /// Optional ?authorId filter. GET /api/identified
    /// </summary>
    [HttpGet]
    public async Task<IReadOnlyList<IdentifiedRow>> Get([FromQuery] int? authorId = null, CancellationToken ct = default)
    {
        var q = _db.BookContentScans.AsNoTracking()
            .Where(c => !c.Reviewed
                && (c.Isbn != null || c.Title != null || c.Series != null
                    || c.AlsoByTitles != null || c.SeriesCatalogJson != null
                    // An author-only guess is worth reviewing ONLY when the file
                    // isn't already filed under an author. For a file already in an
                    // author folder it just re-confirms what we know ("accept author"
                    // on a file that's already there) — pure noise — so drop it.
                    || (c.Author != null && c.AuthorId == null)));
        if (authorId is int aid) q = q.Where(c => c.AuthorId == aid);

        var rows = await q
            .OrderByDescending(c => c.ScannedAt)
            .Select(c => new
            {
                c.Id, c.FullPath, c.Source, c.AuthorId, c.Isbn, c.Title, c.Author,
                c.Series, c.SeriesPosition, c.AlsoByTitles, c.SeriesCatalogJson, c.ScannedAt,
                LinkedAuthorName = c.AuthorId != null
                    ? _db.Authors.Where(a => a.Id == c.AuthorId).Select(a => a.Name).FirstOrDefault()
                    : null,
                FileId = _db.LocalBookFiles.Where(f => f.FullPath == c.FullPath).Select(f => (int?)f.Id).FirstOrDefault(),
            })
            .Take(2000)
            .ToListAsync(ct);

        return rows.Select(r => new IdentifiedRow(
            r.Id, r.FileId, r.FullPath, FormatOf(r.FullPath), r.Source, r.AuthorId, r.LinkedAuthorName,
            r.Isbn, r.Title, r.Author, r.Series, r.SeriesPosition,
            string.IsNullOrEmpty(r.AlsoByTitles) ? Array.Empty<string>() : r.AlsoByTitles.Split(';'),
            ParseCatalog(r.SeriesCatalogJson),
            r.ScannedAt)).ToList();
    }

    // Bibliography-section headers that name a *category*, not a real series, and
    // so are meaningless without the author (every author has "Novels"). Matched on
    // the normalized form (lowercase, punctuation stripped).
    private static readonly HashSet<string> GenericSeriesNames = new(StringComparer.Ordinal)
    {
        "novels", "novel", "the novels", "stories", "short stories", "short story",
        "novellas", "novella", "books", "the books", "other books", "other titles",
        "other novels", "works", "selected works", "collected works", "collections",
        "collection", "anthologies", "anthology", "omnibus", "omnibuses", "fiction",
        "nonfiction", "non fiction", "standalone", "standalones", "stand alone", "other",
    };

    // Qualifies a series name with the author when the name is a generic category
    // header ("Novels" → "Anne McCaffrey Novels"), so it becomes a distinct,
    // author-specific series instead of colliding with every other author's
    // identically-named bucket. Names that already contain the author, or aren't
    // generic, are returned unchanged.
    private static string QualifySeriesName(string name, string authorName)
    {
        if (string.IsNullOrWhiteSpace(authorName)) return name;
        if (!GenericSeriesNames.Contains(TitleNormalizer.Normalize(name))) return name;
        if (name.Contains(authorName, StringComparison.OrdinalIgnoreCase)) return name;
        return $"{authorName.Trim()} {name}";
    }

    // The series catalogue is stored as JSON; decode it for the client, tolerating
    // a malformed value rather than failing the whole list.
    private static IReadOnlyList<SeriesListingRow> ParseCatalog(string? json)
    {
        if (string.IsNullOrEmpty(json)) return Array.Empty<SeriesListingRow>();
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<SeriesListingRow>>(json)
                   ?? (IReadOnlyList<SeriesListingRow>)Array.Empty<SeriesListingRow>();
        }
        catch (System.Text.Json.JsonException)
        {
            return Array.Empty<SeriesListingRow>();
        }
    }

    public sealed record ApplyCatalogResult(
        int SeriesCreated, int SeriesReused, int BooksLinked, int PositionsFixed,
        int TitlesUnmatched, int TitlesAdded, int SourceBooks);

    // Merges every catalogue an author's scanned books carry into one consensus
    // per series: each book lists a prefix of the series in order, so the longest
    // list reconstructs the full ordering and the shorter ones corroborate it.
    // Titles are deduped case-insensitively, longest-list order wins.
    private static IReadOnlyList<SeriesListingRow> MergeCatalogues(IEnumerable<IReadOnlyList<SeriesListingRow>> catalogues)
    {
        var order = new List<string>();
        var bySeries = new Dictionary<string, (string Series, string? Genre, List<IReadOnlyList<string>> Lists)>(StringComparer.OrdinalIgnoreCase);
        foreach (var cat in catalogues)
            foreach (var listing in cat)
            {
                if (string.IsNullOrWhiteSpace(listing.Series)) continue;
                var key = listing.Series.Trim().ToLowerInvariant();
                if (!bySeries.TryGetValue(key, out var acc))
                {
                    acc = (listing.Series.Trim(), listing.Genre, new List<IReadOnlyList<string>>());
                    order.Add(key);
                }
                else if (acc.Genre is null && listing.Genre is not null)
                {
                    acc = (acc.Series, listing.Genre, acc.Lists);
                }
                acc.Lists.Add(listing.Titles);
                bySeries[key] = acc;
            }

        var result = new List<SeriesListingRow>();
        foreach (var key in order)
        {
            var acc = bySeries[key];
            var consensus = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var list in acc.Lists.OrderByDescending(l => l.Count))
                foreach (var t in list)
                    if (seen.Add(t)) consensus.Add(t);
            result.Add(new SeriesListingRow(acc.Series, acc.Genre, consensus));
        }
        return result;
    }

    /// <summary>
    /// Builds series data for the scan row's author from EVERY scanned book's
    /// catalogue (not just this row): merges them into a consensus order per
    /// series, then creates/reuses each Series and assigns the author's owned
    /// books with the catalogue position. Fills books with no series and corrects
    /// the position of books already in that same series; books already in a
    /// *different* series are left untouched. POST /api/identified/{id}/apply-catalog
    /// </summary>
    [HttpPost("{id:int}/apply-catalog")]
    public async Task<IActionResult> ApplyCatalog(int id, CancellationToken ct)
    {
        var scan = await _db.BookContentScans.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (scan is null) return NotFound(new { error = "Scan row not found." });
        if (scan.AuthorId is not int authorId)
            return BadRequest(new { error = "This file isn't linked to an author, so its series can't be attributed." });

        var author = await _db.Authors.FirstOrDefaultAsync(a => a.Id == authorId, ct);
        if (author is null) return NotFound(new { error = "Linked author not found." });

        var result = await BuildSeriesForAuthorAsync(author, ct);
        if (result is null) return BadRequest(new { error = "No series catalogue was found for this author." });
        return Ok(result);
    }

    public sealed record ApplyCatalogAllResult(
        int AuthorsBuilt, int SeriesCreated, int SeriesReused, int BooksLinked, int PositionsFixed, int TitlesAdded);

    /// <summary>
    /// Bulk version of apply-catalog: builds series for EVERY author that has a
    /// content catalogue. Series-only — it never touches the book-title or author
    /// guesses. POST /api/identified/apply-catalog-all
    /// </summary>
    [HttpPost("apply-catalog-all")]
    public async Task<ActionResult<ApplyCatalogAllResult>> ApplyCatalogAll(CancellationToken ct)
    {
        var authorIds = await _db.BookContentScans
            .Where(c => c.AuthorId != null && c.SeriesCatalogJson != null)
            .Select(c => c.AuthorId!.Value).Distinct().ToListAsync(ct);

        int built = 0, created = 0, reused = 0, linked = 0, fixedPos = 0, added = 0;
        foreach (var aid in authorIds)
        {
            ct.ThrowIfCancellationRequested();
            var author = await _db.Authors.FirstOrDefaultAsync(a => a.Id == aid, ct);
            if (author is null) continue;
            var r = await BuildSeriesForAuthorAsync(author, ct);
            _db.ChangeTracker.Clear(); // keep the tracker from growing across authors
            if (r is null) continue;
            built++;
            created += r.SeriesCreated; reused += r.SeriesReused;
            linked += r.BooksLinked; fixedPos += r.PositionsFixed; added += r.TitlesAdded;
        }
        return Ok(new ApplyCatalogAllResult(built, created, reused, linked, fixedPos, added));
    }

    // Core of apply-catalog for one author: merges every catalogue the author's
    // scanned books carry and builds/updates their series. Returns null when the
    // author has no catalogue. Saves its own changes.
    private async Task<ApplyCatalogResult?> BuildSeriesForAuthorAsync(Author author, CancellationToken ct)
    {
        var authorId = author.Id;

        // Pull EVERY catalogue this author's scanned books carry and merge them —
        // book 3 lists 1–2, book 36 lists 1–35, so together they give the full,
        // correctly-ordered series.
        var sourceRows = await _db.BookContentScans
            .Where(c => c.AuthorId == authorId && c.SeriesCatalogJson != null)
            .ToListAsync(ct);
        var merged = MergeCatalogues(sourceRows.Select(r => ParseCatalog(r.SeriesCatalogJson)));
        if (merged.Count == 0) return null;

        // The author's books, indexed by normalized title (exact) with the full
        // list kept for a fuzzy fallback. Series is included so we can tell
        // whether a matched book is already in the series we're building.
        var books = await _db.Books.Include(b => b.Series).Where(b => b.AuthorId == authorId).ToListAsync(ct);
        var normTitles = books.ToDictionary(b => b, b => b.NormalizedTitle ?? TitleNormalizer.Normalize(b.Title));
        var byTitle = new Dictionary<string, Book>();
        foreach (var b in books) byTitle.TryAdd(normTitles[b], b);
        var used = new HashSet<int>(); // each book claimed by at most one title this run

        // Existing work keys (so minted placeholder keys don't collide) and the
        // placeholder books created this run, keyed by normalized title (so the same
        // catalogue title appearing under two listings isn't created twice).
        var usedKeys = new HashSet<string>(books.Select(b => b.OpenLibraryWorkKey ?? string.Empty), StringComparer.Ordinal);
        var placeholdersByNorm = new Dictionary<string, Book>(StringComparer.Ordinal);

        string MintManualKey()
        {
            string k;
            do { k = ManualWorkKey.NewCandidate(); } while (!usedKeys.Add(k));
            return k;
        }

        Book? Resolve(string normTitle)
        {
            if (byTitle.TryGetValue(normTitle, out var exact) && !used.Contains(exact.Id)) return exact;
            Book? best = null;
            var bestScore = CatalogFuzzyFloor;
            foreach (var b in books)
            {
                if (used.Contains(b.Id)) continue;
                var score = FuzzyScore.JaroWinkler(normTitle, normTitles[b]);
                if (score > bestScore) { bestScore = score; best = b; }
            }
            return best;
        }

        // Series resolved this run, keyed by normalized name — so two listings that
        // normalize to the same name reuse the one we just added rather than each
        // querying the not-yet-saved DB and creating a duplicate.
        var seriesByNorm = new Dictionary<string, Series>(StringComparer.Ordinal);

        int created = 0, reused = 0, linked = 0, fixedPos = 0, unmatched = 0, added = 0;
        foreach (var listing in merged)
        {
            if (string.IsNullOrWhiteSpace(listing.Series)) continue;
            // A generic bibliography header like "Novels" / "Short Stories" isn't a
            // real, distinct series — qualify it with the author so it doesn't
            // collide with (and get merged into) some other author's "Novels".
            var name = QualifySeriesName(listing.Series.Trim(), author.Name);
            var normName = TitleNormalizer.Normalize(name);

            if (!seriesByNorm.TryGetValue(normName, out var series))
            {
                // A content catalogue is THIS author's own bibliography, so only reuse
                // a series that is already this author's (or unattributed). Matching
                // purely on name would reuse a different author's identically-named
                // series — dumping this author's books there and leaving no series
                // under this author, so it "never appears" on the Series page even for
                // a unique name.
                series = await _db.Series.FirstOrDefaultAsync(
                    s => s.NormalizedName == normName
                         && (s.PrimaryAuthorId == authorId || s.PrimaryAuthorId == null), ct);
                if (series is null)
                {
                    series = new Series { Name = name, NormalizedName = normName, PrimaryAuthorId = authorId };
                    _db.Series.Add(series);
                    created++;
                }
                else
                {
                    reused++;
                    if (series.PrimaryAuthorId is null) series.PrimaryAuthorId = authorId;
                }
                seriesByNorm[normName] = series;
            }

            for (var i = 0; i < listing.Titles.Count; i++)
            {
                var rawTitle = listing.Titles[i].Trim();
                if (rawTitle.Length == 0) continue;
                var normT = TitleNormalizer.Normalize(rawTitle);
                var pos = (i + 1).ToString();

                var book = Resolve(normT);
                if (book is null)
                {
                    // No existing book for this catalogue title. So the series keeps
                    // its FULL member list (not just the few titles already owned),
                    // mint a placeholder book — a not-yet-owned member with a manual
                    // work key (preserved across OL refreshes) shown as "missing" on
                    // the Series page. Reuse one already minted this run for the title.
                    if (placeholdersByNorm.ContainsKey(normT)) continue;
                    var ph = new Book
                    {
                        AuthorId = authorId,
                        Title = rawTitle,
                        NormalizedTitle = normT,
                        OpenLibraryWorkKey = MintManualKey(),
                        Series = series,
                        SeriesPosition = pos,
                    };
                    _db.Books.Add(ph);
                    placeholdersByNorm[normT] = ph;
                    added++;
                    continue;
                }
                used.Add(book.Id);

                if (book.SeriesId is null && book.Series is null)
                {
                    book.Series = series;          // missing series → fill it
                    book.SeriesPosition = pos;
                    linked++;
                }
                else if (book.Series is not null
                         && TitleNormalizer.Normalize(book.Series.Name) == normName)
                {
                    if (book.SeriesPosition != pos) { book.SeriesPosition = pos; fixedPos++; } // correct the order
                }
                // else: already in a different series — leave it alone.
            }
        }

        // The catalogue rows have now been consumed. Clear the catalogue-only ones
        // (harvested from matched books) from the review list; leave rows that also
        // carry a title/author guess so their file can still be matched.
        foreach (var r in sourceRows)
            if (r.Title is null && r.Author is null && r.Isbn is null && r.Series is null && r.AlsoByTitles is null)
                r.Reviewed = true;

        await _db.SaveChangesAsync(ct);
        return new ApplyCatalogResult(created, reused, linked, fixedPos, unmatched, added, sourceRows.Count);
    }

    /// <summary>
    /// Streams the file for in-browser preview. Works for both tracked files
    /// (in a library folder) and untracked files (in the quarantine folder).
    /// GET /api/identified/{id}/preview?format=epub
    /// </summary>
    [HttpGet("{id:int}/preview")]
    public async Task<IActionResult> Preview(int id, [FromQuery] string format, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(format))
            return BadRequest(new { error = "format query parameter is required" });

        var scan = await _db.BookContentScans.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (scan is null) return NotFound(new { error = "Scan row not found." });
        if (!System.IO.File.Exists(scan.FullPath))
            return NotFound(new { error = "File no longer exists on disk." });

        // Allow access from library locations AND the custom quarantine path.
        var roots = await _db.LibraryLocations.AsNoTracking()
            .Where(l => l.Enabled).Select(l => l.Path).ToListAsync(ct);
        var customUnknown = await UnknownFolderResolver.GetCustomPathAsync(_db, ct);
        var allowedRoots = customUnknown is null
            ? (IReadOnlyList<string>)roots
            : roots.Append(customUnknown).ToList();

        if (!FilePreviewResolver.IsInsideAnyRoot(scan.FullPath, allowedRoots))
            return StatusCode(403, new { error = "Refusing to serve a file outside enabled library locations." });

        var fmt = format.ToLowerInvariant();
        var ext = Path.GetExtension(scan.FullPath).TrimStart('.').ToLowerInvariant();
        var supportedConversions = new[] { "mobi", "azw", "azw3", "fb2", "lit", "docx", "odt" };

        // Convertible formats: serve as EPUB via Calibre. Trigger on either an
        // explicit `epub` request OR the file's own extension, so the client passing
        // the raw format (e.g. ?format=mobi) still gets a rendered EPUB — matching
        // the Untracked page's preview rather than 415-ing.
        if (supportedConversions.Contains(ext) && (fmt == "epub" || fmt == ext))
        {
            var converter = HttpContext.RequestServices.GetRequiredService<CalibreConverter>();
            try
            {
                var convertedPath = await converter.ConvertToEpubAsync(scan.FullPath, ct);
                if (System.IO.File.Exists(convertedPath))
                {
                    var bytes = await System.IO.File.ReadAllBytesAsync(convertedPath, ct);
                    try { System.IO.File.Delete(convertedPath); } catch { /* best effort */ }
                    Response.Headers["Content-Disposition"] =
                        $"inline; filename=\"{Path.GetFileNameWithoutExtension(scan.FullPath)}.epub\"";
                    return File(bytes, "application/epub+zip");
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = $"Conversion to EPUB failed: {ex.Message}" });
            }
        }

        // RTF: convert to plain text on the fly (RtfPipe) for the txt pane, so the
        // reader sees prose, not raw `{\rtf1 …}` markup.
        if (fmt == "rtf")
        {
            try
            {
                var rtf = await System.IO.File.ReadAllTextAsync(scan.FullPath, ct);
                return Content(RtfTextExtractor.ExtractText(rtf), "text/plain; charset=utf-8");
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = $"Could not read RTF: {ex.Message}" });
            }
        }

        var resolution = FilePreviewResolver.Resolve(scan.FullPath, format, allowedRoots);
        if (resolution.Ok is null)
        {
            return resolution.Failure switch
            {
                FilePreviewResolver.FailureKind.UnsupportedFormat
                    => StatusCode(415, new { error = $"Preview not supported for '.{format}'." }),
                FilePreviewResolver.FailureKind.OutsideLibrary
                    => StatusCode(403, new { error = "Refusing to serve a file outside enabled library locations." }),
                _ => NotFound(new { error = $"No '.{format}' file found at this path." }),
            };
        }

        if (!FilePreviewResolver.SupportedFormats.TryGetValue(fmt, out var contentType))
            contentType = "application/octet-stream";

        // Range processing so the browser's native PDF viewer can seek large files.
        return PhysicalFile(resolution.Ok.FullPath, contentType, enableRangeProcessing: true);
    }

    /// <summary>
    /// Overwrites the guessed author name on a scan row with a new free-text
    /// value. The row is NOT marked reviewed — the user still needs to act on
    /// it. PATCH /api/identified/{id}/author
    /// </summary>
    [HttpPatch("{id:int}/author")]
    public async Task<IActionResult> SetAuthor(int id, [FromBody] SetAuthorRequest body, CancellationToken ct)
    {
        var row = await _db.BookContentScans.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (row is null) return NotFound(new { error = "Scan row not found." });
        row.Author = string.IsNullOrWhiteSpace(body.Author) ? null : body.Author.Trim();
        // If the new name matches a watchlist author, link it.
        if (row.Author is not null)
        {
            var match = await _db.Authors
                .FirstOrDefaultAsync(a => a.Name == row.Author, ct);
            row.AuthorId = match?.Id;
        }
        else
        {
            row.AuthorId = null;
        }
        await _db.SaveChangesAsync(ct);
        var linkedName = row.AuthorId is int aid
            ? await _db.Authors.Where(a => a.Id == aid).Select(a => a.Name).FirstOrDefaultAsync(ct)
            : null;
        return Ok(new { author = row.Author, authorId = row.AuthorId, linkedAuthorName = linkedName });
    }

    public sealed record SetAuthorRequest(string? Author);

    // All-optional so [ApiController] can't 400 a partial payload.
    public sealed record UseWorkRequest(
        string? WorkKey, string? Title, int? FirstPublishYear, int? CoverId,
        string? Authors, string? PrimaryAuthorKey, string? PrimaryAuthorName);

    /// <summary>
    /// Matches a scan row's file to an OpenLibrary work the user picked in the
    /// work-search pane — fully applied in one step: the author is resolved (or
    /// created Pending) from the work doc, the Book is ensured, the file moves
    /// into the author's folder linked to that book, and the row leaves the
    /// review list (a series catalogue keeps it for apply-catalog).
    /// POST /api/identified/{id}/use-work
    /// </summary>
    [HttpPost("{id:int}/use-work")]
    public async Task<IActionResult> UseWork(
        int id,
        [FromBody] UseWorkRequest body,
        [FromServices] UntrackedAuthorAssigner assigner,
        CancellationToken ct)
    {
        var row = await _db.BookContentScans.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (row is null) return NotFound(new { error = "Scan row not found." });
        if (string.IsNullOrWhiteSpace(body.WorkKey))
            return BadRequest(new { error = "The selected work carries no OpenLibrary key." });

        var outcome = await assigner.AssignToWorkAsync(
            row,
            body.WorkKey, body.Title, body.FirstPublishYear, body.CoverId,
            body.Authors, body.PrimaryAuthorKey, body.PrimaryAuthorName,
            ct);

        if (!outcome.Assigned)
            return Ok(new { assigned = false, reason = outcome.Reason });
        return Ok(new
        {
            assigned = true,
            authorId = outcome.AuthorId,
            authorName = outcome.AuthorName,
            bookId = outcome.BookId,
            path = outcome.Path,
        });
    }

    /// <summary>Marks a guess reviewed so it leaves the list. POST /api/identified/{id}/dismiss</summary>
    [HttpPost("{id:int}/dismiss")]
    public async Task<IActionResult> Dismiss(int id, CancellationToken ct)
    {
        var row = await _db.BookContentScans.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (row is null) return NotFound(new { error = "Not found." });
        row.Reviewed = true;
        await _db.SaveChangesAsync(ct);
        return Ok(new { reviewed = true });
    }

    /// <summary>
    /// Accepts the matched author for a scan row that has an author but no title.
    /// Assigns the LocalBookFile to the author, moves it on disk into the author's
    /// library folder, clears BookId so it shows in the author's Unmatched section,
    /// and marks the scan row reviewed. POST /api/identified/{id}/accept-author
    /// </summary>
    [HttpPost("{id:int}/accept-author")]
    public async Task<IActionResult> AcceptAuthor(int id, CancellationToken ct)
    {
        var scan = await _db.BookContentScans.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (scan is null) return NotFound(new { error = "Scan row not found." });
        if (scan.AuthorId is null) return BadRequest(new { error = "This scan row has no linked author." });

        var author = await _db.Authors.FirstOrDefaultAsync(a => a.Id == scan.AuthorId, ct);
        if (author is null) return NotFound(new { error = "Linked author not found." });

        // Look up the LocalBookFile by path (same join used in GET).
        var file = await _db.LocalBookFiles
            .FirstOrDefaultAsync(f => f.FullPath == scan.FullPath, ct);
        if (file is null) return NotFound(new { error = "No tracked file found at the scanned path." });

        var roots = await _db.LibraryLocations.AsNoTracking()
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);

        // Find which library root the file currently lives under.
        var root = roots.FirstOrDefault(r =>
            file.FullPath.StartsWith(
                r.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase));
        if (root is null) return BadRequest(new { error = "File is outside all enabled library locations." });

        // Determine the author folder name (or create one from the author's name).
        var authorFolderName = author.CalibreFolderName ?? SanitizeSegment(author.Name);
        if (string.IsNullOrWhiteSpace(author.CalibreFolderName))
            author.CalibreFolderName = authorFolderName;

        var libRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var leafName = System.IO.Path.GetFileName(
            file.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var destPath = System.IO.Path.Combine(libRoot, authorFolderName, leafName);

        string finalPath = file.FullPath;
        var alreadyThere = string.Equals(
            System.IO.Path.GetFullPath(file.FullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            System.IO.Path.GetFullPath(destPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

        if (!alreadyThere)
        {
            var destDir = System.IO.Path.GetDirectoryName(destPath)!;
            Directory.CreateDirectory(destDir);

            try
            {
                if (System.IO.File.Exists(file.FullPath))
                {
                    finalPath = UniqueFilePath(destPath);
                    SafeMove.File(file.FullPath, finalPath);
                    PruneEmptyParent(System.IO.Path.GetDirectoryName(file.FullPath), libRoot);
                }
                else if (Directory.Exists(file.FullPath))
                {
                    finalPath = UniqueDirectoryPath(destDir, leafName);
                    SafeMove.Directory(file.FullPath, finalPath);
                    PruneEmptyParent(System.IO.Path.GetDirectoryName(file.FullPath), libRoot);
                }
                else
                {
                    return BadRequest(new { error = "File no longer exists on disk." });
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return StatusCode(500, new { error = $"Failed to move file: {ex.Message}" });
            }
        }

        // Update the DB record.
        file.FullPath = finalPath;
        file.AuthorId = author.Id;
        file.AuthorFolder = authorFolderName;
        file.BookId = null;
        file.ManuallyUnmatched = false;
        if (!alreadyThere)
            file.ResetIntegrity(); // moved into the author folder — re-check it there

        // Mark the scan row reviewed so it leaves the list, and keep its path
        // current — a stale path would make the row look like the file vanished.
        scan.Reviewed = true;
        scan.FullPath = finalPath;

        await _db.SaveChangesAsync(ct);

        return Ok(new { authorId = author.Id, authorName = author.Name, finalPath });
    }

    // Sanitise a name so it can be used as a folder name (strips illegal chars).
    private static string SanitizeSegment(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c)).Trim();
    }

    private static string UniqueFilePath(string path)
    {
        if (!System.IO.File.Exists(path)) return path;
        var dir = System.IO.Path.GetDirectoryName(path)!;
        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
        var ext = System.IO.Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var candidate = System.IO.Path.Combine(dir, $"{stem}_{n}{ext}");
            if (!System.IO.File.Exists(candidate)) return candidate;
        }
    }

    private static string UniqueDirectoryPath(string parent, string leaf)
    {
        var candidate = System.IO.Path.Combine(parent, leaf);
        if (!Directory.Exists(candidate)) return candidate;
        for (var n = 2; ; n++)
        {
            candidate = System.IO.Path.Combine(parent, $"{leaf}_{n}");
            if (!Directory.Exists(candidate)) return candidate;
        }
    }

    private static void PruneEmptyParent(string? dir, string stopRoot)
    {
        var stop = System.IO.Path.GetFullPath(stopRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = dir;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var full = System.IO.Path.GetFullPath(current)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!full.StartsWith(stop, StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, stop, StringComparison.OrdinalIgnoreCase))
                break;
            if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any())
            {
                try { Directory.Delete(full); current = System.IO.Path.GetDirectoryName(full); }
                catch { break; }
            }
            else break;
        }
    }

    private static string? FormatOf(string fullPath)
    {
        var ext = Path.GetExtension(fullPath).TrimStart('.').ToLowerInvariant();
        return string.IsNullOrEmpty(ext) ? null : ext;
    }
}

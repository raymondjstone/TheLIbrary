using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.Incoming;
using TheLibrary.Server.Services.IO;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Services.Scheduling;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthorsController : ControllerBase
{
    private readonly LibraryDbContext _db;
    private readonly OpenLibraryClient _ol;
    private readonly AuthorRefresher _refresher;
    private readonly ManualBookService _manualBooks;
    private readonly IFileSystem _fs;
    private readonly ILogger<AuthorsController> _log;

    public AuthorsController(
        LibraryDbContext db,
        OpenLibraryClient ol,
        AuthorRefresher refresher,
        ManualBookService manualBooks,
        IFileSystem fs,
        ILogger<AuthorsController> log)
    {
        _db = db;
        _ol = ol;
        _refresher = refresher;
        _manualBooks = manualBooks;
        _fs = fs;
        _log = log;
    }

    public sealed record AuthorListItem(
        int Id,
        string Name,
        string? CalibreFolderName,
        string? OpenLibraryKey,
        string Status,
        string? ExclusionReason,
        int Priority,
        int BookCount,
        int OwnedCount,
        int EbookOwnedCount,
        int PhysicalOwnedCount,
        DateTime? LastSyncedAt);

    // Keyless projection used by the raw SQL book-stats query below.
    private sealed record BookStatRow(int AuthorId, int Total, int Ebook, int Physical);

    // Returns the full list, unsorted and unfiltered. The client caches it
    // and applies filter/sort and paging in the browser.
    [HttpGet]
    public async Task<IReadOnlyList<AuthorListItem>> List(CancellationToken ct)
    {
        // Non-pen-name duplicates are folded into their canonical's view and
        // hidden from the main list. Pen-name children stay visible — they keep
        // their own listing and just back-reference the canonical on detail.
        var baseRows = await _db.Authors.AsNoTracking()
            .Where(a => a.LinkedToAuthorId == null || a.IsPenName)
            .Select(a => new
            {
                a.Id,
                a.Name,
                a.CalibreFolderName,
                a.OpenLibraryKey,
                a.Status,
                a.ExclusionReason,
                a.Priority,
                a.LastSyncedAt
            })
            .ToListAsync(ct);

        // A single pass with a hash join is faster than LINQ GroupBy with
        // Count(b => b.LocalFiles.Any()), which EF emits as an EXISTS
        // subquery evaluated per book row. The LEFT JOIN materialises the
        // distinct ebook-owned book ids once, then SQL Server hash-joins
        // it with Books and groups — one row per author out.
        var stats = await _db.Database
            .SqlQuery<BookStatRow>($"""
                SELECT b.AuthorId,
                       COUNT(*)                                                               AS Total,
                       COUNT(lf.BookId)                                                      AS Ebook,
                       SUM(CASE WHEN lf.BookId IS NULL AND b.ManuallyOwned = 1 THEN 1 ELSE 0 END) AS Physical
                FROM   Books b
                LEFT JOIN (
                    SELECT DISTINCT BookId
                    FROM   LocalBookFiles
                    WHERE  BookId IS NOT NULL
                ) lf ON lf.BookId = b.Id
                GROUP BY b.AuthorId
                """)
            .ToDictionaryAsync(x => x.AuthorId, ct);

        // Fold stats from non-pen-name children into their canonical's totals so
        // book counts reflect the merged view that the detail page renders.
        var hiddenChildren = await _db.Authors.AsNoTracking()
            .Where(a => a.LinkedToAuthorId != null && !a.IsPenName)
            .Select(a => new { a.Id, CanonicalId = a.LinkedToAuthorId!.Value })
            .ToListAsync(ct);
        foreach (var child in hiddenChildren)
        {
            if (!stats.TryGetValue(child.Id, out var childStats)) continue;
            stats[child.CanonicalId] = stats.TryGetValue(child.CanonicalId, out var parent)
                ? new BookStatRow(child.CanonicalId,
                    parent.Total + childStats.Total,
                    parent.Ebook + childStats.Ebook,
                    parent.Physical + childStats.Physical)
                : new BookStatRow(child.CanonicalId, childStats.Total, childStats.Ebook, childStats.Physical);
        }

        return baseRows.Select(r =>
        {
            stats.TryGetValue(r.Id, out var s);
            var ebook    = s?.Ebook    ?? 0;
            var physical = s?.Physical ?? 0;
            return new AuthorListItem(
                r.Id, r.Name, r.CalibreFolderName, r.OpenLibraryKey,
                r.Status.ToString(), r.ExclusionReason,
                r.Priority, s?.Total ?? 0,
                OwnedCount: ebook + physical,
                ebook, physical, r.LastSyncedAt);
        }).ToList();
    }

    public sealed record StarredAuthorRow(
        int Id, string Name, int Priority,
        int BookCount, int EbookCount, int UnmatchedCount);

    [HttpGet("starred")]
    public async Task<IReadOnlyList<StarredAuthorRow>> Starred(CancellationToken ct)
    {
        var authors = await _db.Authors.AsNoTracking()
            .Where(a => a.Priority >= 1)
            .OrderByDescending(a => a.Priority).ThenBy(a => a.Name)
            .Select(a => new { a.Id, a.Name, a.Priority })
            .ToListAsync(ct);

        if (authors.Count == 0) return Array.Empty<StarredAuthorRow>();

        var ids = authors.Select(a => a.Id).ToList();

        var bookCounts = await _db.Books.AsNoTracking()
            .Where(b => ids.Contains(b.AuthorId))
            .GroupBy(b => b.AuthorId)
            .Select(g => new { AuthorId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AuthorId, x => x.Count, ct);

        var ebookCounts = await _db.Books.AsNoTracking()
            .Where(b => ids.Contains(b.AuthorId) && b.LocalFiles.Any())
            .GroupBy(b => b.AuthorId)
            .Select(g => new { AuthorId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AuthorId, x => x.Count, ct);

        var unmatchedRaw = await _db.LocalBookFiles.AsNoTracking()
            .Where(f => f.BookId == null && f.AuthorId != null && ids.Contains(f.AuthorId.Value))
            .GroupBy(f => f.AuthorId)
            .Select(g => new { AuthorId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var unmatchedDict = unmatchedRaw.ToDictionary(x => x.AuthorId!.Value, x => x.Count);

        return authors.Select(a => new StarredAuthorRow(
            a.Id, a.Name, a.Priority,
            bookCounts.GetValueOrDefault(a.Id),
            ebookCounts.GetValueOrDefault(a.Id),
            unmatchedDict.GetValueOrDefault(a.Id)
        )).ToList();
    }

    public sealed record SeriesSuggestion(int Id, string Name, string? PrimaryAuthorName);

    // Lightweight reference shown next to the canonical / linked authors on the
    // detail page.
    public sealed record LinkedAuthorRef(int Id, string Name, bool IsPenName);

    public sealed record AuthorDetail(
        int Id,
        string Name,
        string? OpenLibraryKey,
        string? CalibreFolderName,
        string Status,
        string? ExclusionReason,
        int Priority,
        DateTime? LastSyncedAt,
        DateTime? NextFetchAt,
        int? RefreshIntervalDays,
        string? Bio,
        string? Notes,
        bool NotifyOnNewBooks,
        IReadOnlyList<BookRow> Books,
        IReadOnlyList<UnmatchedRow> UnmatchedLocal,
        IReadOnlyList<SeriesSuggestion> AssociatedSeries,
        // null when this author isn't linked to anyone. Non-null when this is a
        // child entry — IsPenName tells the UI whether to show a "pen name of"
        // banner (true) or a "duplicate of" / "see canonical" banner (false).
        LinkedAuthorRef? LinkedTo,
        // Children that fold INTO this author when this is the canonical. The
        // books / unmatched files of these authors are merged into the lists
        // above; the UI uses Alternates only to render a navigation list.
        IReadOnlyList<LinkedAuthorRef> Alternates,
        // Pen-name children — listed for UI navigation but NOT folded in. The
        // user's books pages keep them separate.
        IReadOnlyList<LinkedAuthorRef> PenNames);

    public sealed record BookRow(
        int Id,
        string Title,
        string? NormalizedTitle,
        int? FirstPublishYear,
        int? CoverId,
        string OpenLibraryWorkKey,
        bool Owned,
        bool ManuallyOwned,
        bool HasLocalFiles,
        string ReadStatus,
        DateTime? ReadAt,
        bool Wanted,
        string? Subjects,
        string? Series,
        string? SeriesPosition,
        IReadOnlyList<LocalFileRow> Files,
        int? SeriesId = null,
        string? SeriesPrimaryAuthorName = null,
        string? CoverUrl = null,
        int AuthorId = 0,
        bool Suppressed = false);

    public sealed record LocalFileRow(int Id, string FullPath, IReadOnlyList<string> Formats);

    public sealed record UnmatchedRow(int Id, string TitleFolder, string FullPath, IReadOnlyList<string> Formats);

    public sealed record OpenLibraryWorkCandidate(
        string Key, string Title, int? FirstPublishYear, int? CoverId, string? Authors,
        string? PrimaryAuthorKey, string? PrimaryAuthorName);

    public sealed record AddOpenLibraryBookRequest(
        string WorkKey,
        string? Title,
        int? FirstPublishYear,
        int? CoverId,
        string? Authors,
        bool Owned);

    public sealed record MatchOpenLibraryFileRequest(
        string WorkKey,
        string? Title,
        int? FirstPublishYear,
        int? CoverId,
        string? Authors,
        string? PrimaryAuthorKey,
        string? PrimaryAuthorName);

    public sealed record BulkMatchOpenLibraryFileRequest(IReadOnlyList<int> FileIds);
    public sealed record BulkMatchOpenLibraryFileResult(int Matched, IReadOnlyList<string> Errors);

    // Extensions we recognize as ebook files in the incoming pipeline. Any
    // other file in the title folder (cover.jpg, metadata.opf, …) is ignored
    // so the UI doesn't show "jpg" as a sendable format.
    private static readonly HashSet<string> EbookExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".mobi", ".azw", ".azw3", ".azw4", ".kf8", ".prc", ".pdb",
        ".fb2", ".fbz", ".pdf", ".lit", ".cbz", ".docx", ".odt", ".txt"
    };

    // FullPath may be a file path (flat-file layout after organizer runs) or a
    // directory path (classic Calibre layout). Handle both so the UI shows formats.
    private static IReadOnlyList<string> FormatsInFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Array.Empty<string>();
        // FullPath may point at a single file (flat-file layout: one
        // LocalBookFile row per file) or at a title directory (classic Calibre
        // layout: one row for the title folder containing every format). Each
        // row must report only what it actually represents — DO NOT scan
        // siblings when the path is a file, or every row in a multi-format
        // flat-file folder would falsely advertise the same set and clicking
        // one row's Send button would look identical to clicking another's.
        if (System.IO.File.Exists(path))
        {
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            return EbookExtensions.Contains("." + ext) ? new[] { ext } : Array.Empty<string>();
        }
        if (!Directory.Exists(path)) return Array.Empty<string>();
        try
        {
            return Directory.EnumerateFiles(path)
                .Select(p => Path.GetExtension(p).TrimStart('.').ToLowerInvariant())
                .Where(ext => EbookExtensions.Contains("." + ext))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(ext => ext)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<AuthorDetail>> Get(int id, CancellationToken ct)
    {
        var a = await _db.Authors.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return NotFound();

        // Resolve link state. The canonical author (if any) is whoever this row
        // ultimately points at via LinkedToAuthorId.
        LinkedAuthorRef? linkedTo = null;
        if (a.LinkedToAuthorId is int linkId)
        {
            var canon = await _db.Authors.AsNoTracking()
                .Where(x => x.Id == linkId)
                .Select(x => new { x.Id, x.Name })
                .FirstOrDefaultAsync(ct);
            if (canon is not null)
                linkedTo = new LinkedAuthorRef(canon.Id, canon.Name, a.IsPenName);
        }

        // Children that point AT this author. Split into:
        //   Alternates — fold into this author's view (books + unmatched files)
        //   PenNames   — listed but kept separate
        var children = await _db.Authors.AsNoTracking()
            .Where(x => x.LinkedToAuthorId == id)
            .OrderBy(x => x.Name)
            .Select(x => new { x.Id, x.Name, x.IsPenName, x.CalibreFolderName })
            .ToListAsync(ct);
        var alternateIds = children.Where(c => !c.IsPenName).Select(c => c.Id).ToList();

        // Author ids whose books / unmatched files should appear under this view.
        // For canonical: this author + non-pen-name children. For a non-pen-name
        // child: nothing (the child's books surface under its canonical, so the
        // detail page is just a redirect surface). For a pen name: just itself.
        var foldedIds = new List<int> { id };
        var foldedFolderCandidates = FolderCandidatesFor(a);
        if (linkedTo is null || linkedTo.IsPenName)
        {
            foldedIds.AddRange(alternateIds);
            foreach (var child in children.Where(c => !c.IsPenName))
            {
                var stub = new Author { Name = child.Name, CalibreFolderName = child.CalibreFolderName };
                foreach (var f in FolderCandidatesFor(stub))
                    if (!foldedFolderCandidates.Contains(f, StringComparer.OrdinalIgnoreCase))
                        foldedFolderCandidates.Add(f);
            }
        }
        else
        {
            // Non-pen-name child — its own books / files all live under the canonical
            // now. Show an empty page so the user navigates to the canonical instead.
            foldedIds.Clear();
            foldedFolderCandidates = new List<string>();
        }

        // Projected into an intermediate shape so we can enumerate the
        // per-file folder on disk for formats outside the EF query.
        var rawBooks = foldedIds.Count == 0
            ? new()
            : await _db.Books.AsNoTracking()
                .Where(b => foldedIds.Contains(b.AuthorId))
                .OrderBy(b => b.SeriesId == null ? 1 : 0)
                .ThenBy(b => b.Series!.Name)
                .ThenBy(b => b.SeriesPosition)
                .ThenBy(b => b.FirstPublishYear ?? int.MaxValue)
                .ThenBy(b => b.Title)
                .Select(b => new
                {
                    b.Id, b.Title, b.NormalizedTitle, b.FirstPublishYear, b.CoverId, b.CoverUrl, b.OpenLibraryWorkKey,
                    b.AuthorId, b.ManuallyOwned, b.ReadStatus, b.ReadAt, b.Wanted, b.Subjects, b.Suppressed,
                    SeriesName = b.Series != null ? b.Series.Name : null,
                    b.SeriesId,
                    SeriesPrimaryAuthorName = b.Series != null && b.Series.PrimaryAuthor != null ? b.Series.PrimaryAuthor.Name : null,
                    b.SeriesPosition,
                    Files = b.LocalFiles.Select(f => new { f.Id, f.FullPath }).ToList()
                })
                .ToListAsync(ct);

        var books = rawBooks.Select(b => new BookRow(
            b.Id, b.Title, b.NormalizedTitle, b.FirstPublishYear, b.CoverId, b.OpenLibraryWorkKey,
            b.ManuallyOwned || b.Files.Count > 0,
            b.ManuallyOwned,
            b.Files.Count > 0,
            b.ReadStatus.ToString(),
            b.ReadAt,
            b.Wanted,
            b.Subjects,
            b.SeriesName,
            b.SeriesPosition,
            b.Files.Select(f => new LocalFileRow(f.Id, f.FullPath, FormatsInFolder(f.FullPath))).ToList(),
            b.SeriesId,
            b.SeriesPrimaryAuthorName,
            b.CoverUrl,
            b.AuthorId,
            b.Suppressed
        )).ToList();

        // Include orphan rows (AuthorId == null) whose Calibre folder matches
        // any of the folded authors by name or recorded folder. Happens when the
        // user adds the author to the watchlist after sync had already recorded
        // files into the orphan pool.
        var rawUnmatched = foldedIds.Count == 0
            ? new()
            : await _db.LocalBookFiles.AsNoTracking()
                .Where(f => f.BookId == null
                    && (foldedIds.Contains(f.AuthorId ?? -1)
                        || (f.AuthorId == null && foldedFolderCandidates.Contains(f.AuthorFolder))))
                .OrderBy(f => f.TitleFolder)
                .Select(f => new { f.Id, f.TitleFolder, f.FullPath })
                .ToListAsync(ct);

        var unmatched = rawUnmatched
            .Select(f => new UnmatchedRow(f.Id, f.TitleFolder, f.FullPath, FormatsInFolder(f.FullPath)))
            .ToList();

        // Include the canonical's own series AND any series whose primary or
        // co-author is one of the non-pen-name children folded into this view.
        // The series picker on the merged page would otherwise miss series the
        // child author was primary on.
        var seriesAuthorIds = new List<int>(foldedIds);
        var associatedSeries = await _db.Series
            .Where(s => seriesAuthorIds.Contains(s.PrimaryAuthorId ?? -1)
                     || s.SeriesAuthors.Any(sa => seriesAuthorIds.Contains(sa.AuthorId)))
            .OrderBy(s => s.Name)
            .Select(s => new SeriesSuggestion(s.Id, s.Name, s.PrimaryAuthor != null ? s.PrimaryAuthor.Name : null))
            .ToListAsync(ct);

        var alternates = children
            .Where(c => !c.IsPenName)
            .Select(c => new LinkedAuthorRef(c.Id, c.Name, false))
            .ToList();
        var penNames = children
            .Where(c => c.IsPenName)
            .Select(c => new LinkedAuthorRef(c.Id, c.Name, true))
            .ToList();

        return new AuthorDetail(
            a.Id, a.Name, a.OpenLibraryKey, a.CalibreFolderName,
            a.Status.ToString(), a.ExclusionReason, a.Priority, a.LastSyncedAt, a.NextFetchAt, a.RefreshIntervalDays,
            a.Bio, a.Notes, a.NotifyOnNewBooks,
            books, unmatched, associatedSeries,
            linkedTo, alternates, penNames);
    }

    private static List<string> FolderCandidatesFor(Author a)
    {
        var list = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(a.Name)) list.Add(a.Name);
        if (!string.IsNullOrWhiteSpace(a.CalibreFolderName)
            && !list.Contains(a.CalibreFolderName, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(a.CalibreFolderName);
        }
        return list;
    }

    // True when the local file is either already linked to this author or is
    // an orphan row whose Calibre folder matches the author. Endpoints that
    // act on unmatched files use this so the tolerance of Get() extends to
    // match/unmatch/return-to-incoming.
    private static bool FileBelongsToAuthor(LocalBookFile file, Author author)
    {
        if (file.AuthorId == author.Id) return true;
        if (file.AuthorId != null) return false;
        foreach (var folder in FolderCandidatesFor(author))
            if (string.Equals(folder, file.AuthorFolder, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public sealed record BookSuggestion(int BookId, string Title, double Score, string? Series, string? SeriesPosition);
    public sealed record FileSuggestionSet(int FileId, string InferredTitle, IReadOnlyList<BookSuggestion> Candidates);

    // Returns top-N candidate books per unmatched file. The same author-prefix
    // strip + series-filename parsing that the sync matcher uses runs here so
    // suggestions match the title the user expects to see. Candidates are
    // scored with Jaro-Winkler against the inferred title; the response is
    // ordered by score descending.
    [HttpGet("{id:int}/unmatched/suggestions")]
    public async Task<ActionResult<IReadOnlyList<FileSuggestionSet>>> Suggestions(
        int id, CancellationToken ct, [FromQuery] int top = 3)
    {
        if (top < 1 || top > 10) top = 3;

        var author = await _db.Authors.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (author is null) return NotFound(new { error = "Author not found" });

        // Folded book set: own + non-pen-name children's books.
        var foldedIds = new List<int> { id };
        foldedIds.AddRange(await _db.Authors.AsNoTracking()
            .Where(a => a.LinkedToAuthorId == id && !a.IsPenName)
            .Select(a => a.Id)
            .ToListAsync(ct));

        var books = await _db.Books.AsNoTracking()
            .Where(b => foldedIds.Contains(b.AuthorId))
            .Select(b => new
            {
                b.Id, b.Title, b.NormalizedTitle,
                SeriesName = b.Series != null ? b.Series.Name : null,
                b.SeriesPosition,
            })
            .ToListAsync(ct);

        var folderCandidates = FolderCandidatesFor(author);
        var unmatched = await _db.LocalBookFiles.AsNoTracking()
            .Where(f => f.BookId == null
                && (foldedIds.Contains(f.AuthorId ?? -1)
                    || (f.AuthorId == null && folderCandidates.Contains(f.AuthorFolder))))
            .Select(f => new { f.Id, f.TitleFolder, f.NormalizedTitle })
            .ToListAsync(ct);

        var result = new List<FileSuggestionSet>(unmatched.Count);
        foreach (var file in unmatched)
        {
            // Use the same stem-candidate pipeline the sync matcher does so
            // the inferred title shown in the UI matches what auto-matching
            // would have used. The first non-empty candidate is the
            // "preferred" inferred title to display.
            var stem = file.TitleFolder ?? "";
            var stems = SyncService.TitleStemCandidates(stem, author).ToList();
            var inferredRaw = stems.FirstOrDefault() ?? stem;
            var inferredNormalised = TitleNormalizer.Normalize(inferredRaw);

            var scored = books
                .Select(b => new BookSuggestion(
                    b.Id, b.Title,
                    FuzzyScore.JaroWinkler(b.NormalizedTitle ?? "", inferredNormalised),
                    b.SeriesName, b.SeriesPosition))
                .OrderByDescending(s => s.Score)
                .Take(top)
                .Where(s => s.Score >= 0.5)
                .ToList();

            result.Add(new FileSuggestionSet(file.Id, inferredRaw, scored));
        }
        return Ok(result);
    }

    public sealed record BulkMatchRequest(IReadOnlyList<BulkMatchItem> Items);
    public sealed record BulkMatchItem(int FileId, int BookId);
    public sealed record BulkMatchSummary(int Matched, int Skipped, IReadOnlyList<string> Errors);

    // Applies a batch of (FileId, BookId) pairs in one call so the UI can offer
    // a "Confirm all high-confidence matches" button. Per-row validation is the
    // same as POST /unmatched/{fileId}/match — book must belong to the author
    // or one of its non-pen-name children; file must already be associated.
    [HttpPost("{id:int}/unmatched/bulk-match")]
    public async Task<ActionResult<BulkMatchSummary>> BulkMatch(
        int id, [FromBody] BulkMatchRequest body, CancellationToken ct)
    {
        var author = await _db.Authors.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (author is null) return NotFound(new { error = "Author not found" });

        int matched = 0, skipped = 0;
        var errors = new List<string>();
        foreach (var item in body.Items ?? Array.Empty<BulkMatchItem>())
        {
            var file = await _db.LocalBookFiles.FirstOrDefaultAsync(f => f.Id == item.FileId, ct);
            if (file is null) { errors.Add($"file {item.FileId}: not found"); skipped++; continue; }
            if (!FileBelongsToAuthor(file, author)) { errors.Add($"file {item.FileId}: not under this author"); skipped++; continue; }

            var book = await _db.Books.FirstOrDefaultAsync(b => b.Id == item.BookId, ct);
            if (book is null) { errors.Add($"book {item.BookId}: not found"); skipped++; continue; }
            if (!await BookBelongsToAuthorViewAsync(book, id, ct))
            { errors.Add($"book {item.BookId}: not under this author"); skipped++; continue; }

            file.AuthorId = id;
            file.BookId = book.Id;
            file.ManuallyUnmatched = false;
            matched++;
        }
        if (matched > 0) await _db.SaveChangesAsync(ct);
        return Ok(new BulkMatchSummary(matched, skipped, errors));
    }

    public sealed record AdditionalBooksRequest(IReadOnlyList<int> BookIds);

    // For omnibus / boxed-set files that represent multiple books, the user can
    // attach extra BookIds beyond the primary one set via /match. Stored as a
    // comma-separated string on LocalBookFile so reads stay cheap and we don't
    // need a join table for the secondary use case.
    [HttpPut("{id:int}/unmatched/{fileId:int}/additional-books")]
    public async Task<IActionResult> SetAdditionalBooks(
        int id, int fileId, [FromBody] AdditionalBooksRequest req, CancellationToken ct)
    {
        var author = await _db.Authors.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (author is null) return NotFound(new { error = "Author not found" });

        var file = await _db.LocalBookFiles.FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null) return NotFound(new { error = "Local file not found" });
        if (!FileBelongsToAuthor(file, author))
            return BadRequest(new { error = "File does not belong to this author" });

        var ids = (req.BookIds ?? Array.Empty<int>())
            .Where(b => b > 0 && b != file.BookId)
            .Distinct()
            .ToList();

        // Validate each book belongs to the author's view.
        foreach (var bookId in ids)
        {
            var book = await _db.Books.FirstOrDefaultAsync(b => b.Id == bookId, ct);
            if (book is null) return BadRequest(new { error = $"Book {bookId} not found" });
            if (!await BookBelongsToAuthorViewAsync(book, id, ct))
                return BadRequest(new { error = $"Book {bookId} does not belong to this author" });
        }

        file.AdditionalBookIds = ids.Count == 0 ? null : string.Join(",", ids);
        await _db.SaveChangesAsync(ct);
        return Ok(new { file.Id, file.BookId, AdditionalBookIds = ids });
    }

    public sealed record MatchLocalFileRequest(int BookId);

    // Manually link an unmatched local file to one of this author's tracked
    // works — used when the automatic title match didn't catch a variant
    // spelling / punctuation the scanner couldn't normalize. Both the file
    // and the book must already be associated with this author.
    [HttpPost("{id:int}/unmatched/{fileId:int}/match")]
    public async Task<ActionResult<AuthorDetail>> MatchLocalFile(
        int id, int fileId, [FromBody] MatchLocalFileRequest body, CancellationToken ct)
    {
        var author = await _db.Authors.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (author is null) return NotFound(new { error = "Author not found" });

        var file = await _db.LocalBookFiles.FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null) return NotFound(new { error = "Local file not found" });
        if (!FileBelongsToAuthor(file, author))
            return BadRequest(new { error = "File does not belong to this author" });

        var book = await _db.Books.FirstOrDefaultAsync(b => b.Id == body.BookId, ct);
        if (book is null) return NotFound(new { error = "Book not found" });
        if (!await BookBelongsToAuthorViewAsync(book, id, ct))
            return BadRequest(new { error = "Book does not belong to this author" });

        file.AuthorId = id;
        file.BookId = book.Id;
        file.ManuallyUnmatched = false;
        await _db.SaveChangesAsync(ct);
        return await Get(id, ct);
    }

    // A book "belongs to" the author's view when its AuthorId is the author
    // itself OR a non-pen-name child folded into that view. Pen-name children
    // are kept separate so their books are intentionally NOT part of the
    // canonical's view and don't pass this gate.
    private async Task<bool> BookBelongsToAuthorViewAsync(Book book, int authorId, CancellationToken ct)
    {
        if (book.AuthorId == authorId) return true;
        return await _db.Authors
            .AnyAsync(a => a.Id == book.AuthorId
                        && a.LinkedToAuthorId == authorId
                        && !a.IsPenName, ct);
    }

    // Undo a match (manual or automatic). The file stays associated with the
    // author but no longer counts as a local copy of any specific work.
    [HttpDelete("{id:int}/unmatched/{fileId:int}/match")]
    public async Task<ActionResult<AuthorDetail>> UnmatchLocalFile(
        int id, int fileId, CancellationToken ct)
    {
        var author = await _db.Authors.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (author is null) return NotFound(new { error = "Author not found" });

        var file = await _db.LocalBookFiles.FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null) return NotFound(new { error = "Local file not found" });
        if (!FileBelongsToAuthor(file, author))
            return BadRequest(new { error = "File does not belong to this author" });

        file.BookId = null;
        file.ManuallyUnmatched = true;
        await _db.SaveChangesAsync(ct);
        return await Get(id, ct);
    }

    // Physically relocates the file's folder back into the incoming bucket
    // so the user can re-trigger incoming processing against an updated
    // author watchlist. The DB row is removed afterward — this file is
    // explicitly no longer "at" the old author/location. Sibling rows
    // pointing into the same folder (multi-format books) are dropped too
    // so the library view doesn't dangle pointers to a moved directory.
    [HttpPost("{id:int}/unmatched/{fileId:int}/return-to-incoming")]
    public async Task<ActionResult<AuthorDetail>> ReturnToIncoming(
        int id, int fileId, CancellationToken ct)
    {
        var author = await _db.Authors.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (author is null) return NotFound(new { error = "Author not found" });

        var file = await _db.LocalBookFiles.FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null) return NotFound(new { error = "Local file not found" });
        if (!FileBelongsToAuthor(file, author))
            return BadRequest(new { error = "File does not belong to this author" });

        var incomingSetting = await _db.AppSettings
            .FirstOrDefaultAsync(s => s.Key == AppSettingKeys.IncomingFolder, ct);
        var incomingPath = incomingSetting?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(incomingPath))
            return BadRequest(new { error = "Incoming folder is not configured" });
        if (!Directory.Exists(incomingPath))
            return BadRequest(new { error = $"Incoming folder does not exist: {incomingPath}" });

        var source = file.FullPath;
        if (!Directory.Exists(source))
            return BadRequest(new { error = $"Source folder no longer exists on disk: {source}" });

        // Use the existing TitleFolder name when we have one (normal Calibre
        // layout), otherwise the AuthorFolder (scanner recorded an empty
        // TitleFolder when the file was directly under the author dir).
        var leafName = !string.IsNullOrWhiteSpace(file.TitleFolder)
            ? file.TitleFolder
            : file.AuthorFolder;
        if (string.IsNullOrWhiteSpace(leafName)) leafName = $"returned-{file.Id}";

        var destPath = UniqueDirectory(incomingPath, leafName);

        try
        {
            Directory.Move(source, destPath);
        }
        catch (IOException ex)
        {
            return StatusCode(500, new { error = $"Move failed: {ex.Message}" });
        }

        // Clean up every DB row whose FullPath is inside the folder we just
        // moved out — otherwise the UI would still show sibling local files
        // pointing at paths that no longer exist.
        var moved = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = moved + Path.DirectorySeparatorChar;
        var stale = await _db.LocalBookFiles
            .Where(f => f.FullPath == moved || f.FullPath.StartsWith(prefix))
            .ToListAsync(ct);
        _db.LocalBookFiles.RemoveRange(stale);

        // If the old author folder is now empty, drop it too — the library
        // scan otherwise leaves an empty directory behind.
        var parent = Path.GetDirectoryName(moved);
        if (!string.IsNullOrWhiteSpace(parent)
            && Directory.Exists(parent)
            && !Directory.EnumerateFileSystemEntries(parent).Any())
        {
            try { Directory.Delete(parent); } catch { /* best effort */ }
        }

        await _db.SaveChangesAsync(ct);
        return await Get(id, ct);
    }

    // Picks "<parent>\<leaf>" if free, else "<parent>\<leaf> (2)", "(3)", …
    private static string UniqueDirectory(string parent, string leaf)
    {
        var safe = SanitizeSegment(leaf);
        var candidate = Path.Combine(parent, safe);
        if (!Directory.Exists(candidate) && !System.IO.File.Exists(candidate)) return candidate;
        for (var i = 2; i < 1000; i++)
        {
            var next = Path.Combine(parent, $"{safe} ({i})");
            if (!Directory.Exists(next) && !System.IO.File.Exists(next)) return next;
        }
        // Extremely unlikely; fall back to a timestamped name.
        return Path.Combine(parent, $"{safe} ({DateTime.UtcNow:yyyyMMddHHmmss})");
    }

    private static readonly HashSet<char> InvalidSegmentChars =
        new(Path.GetInvalidFileNameChars());

    private static string SanitizeSegment(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(InvalidSegmentChars.Contains(c) ? '_' : c);
        var s = sb.ToString().Trim().TrimEnd('.', ' ');
        return string.IsNullOrEmpty(s) ? "returned" : s;
    }

    public sealed class AddAuthorRequest
    {
        [Required]
        [StringLength(64)]
        public string OpenLibraryKey { get; init; } = "";

        [StringLength(512)]
        public string? Name { get; init; }
    }

    // Adds an author to the watchlist from an OpenLibrary key.
    // If Name is omitted we resolve it via OL search.
    [HttpPost]
    public async Task<ActionResult<AuthorListItem>> Add([FromBody] AddAuthorRequest body, CancellationToken ct)
    {
        var key = body.OpenLibraryKey.Trim();
        // Accept "/authors/OL1234A" or "OL1234A".
        if (key.StartsWith("/authors/", StringComparison.OrdinalIgnoreCase))
            key = key[("/authors/".Length)..];

        var existing = await _db.Authors.FirstOrDefaultAsync(a => a.OpenLibraryKey == key, ct);
        if (existing is not null)
        {
            // Already tracked — still run adoption so any unmatched Calibre files
            // get linked immediately without requiring the user to wait for a sync.
            if (string.IsNullOrWhiteSpace(existing.CalibreFolderName))
            {
                var normExisting = TitleNormalizer.NormalizeAuthor(existing.Name);
                var candidateFolders = await _db.LocalBookFiles
                    .Where(f => f.AuthorId == null)
                    .Select(f => f.AuthorFolder)
                    .Distinct()
                    .ToListAsync(ct);
                var matched = candidateFolders
                    .FirstOrDefault(f => TitleNormalizer.NormalizeAuthor(f) == normExisting);
                if (matched is not null)
                {
                    existing.CalibreFolderName = matched;
                    await _db.SaveChangesAsync(ct);
                }
            }
            if (!string.IsNullOrWhiteSpace(existing.CalibreFolderName))
            {
                await _db.LocalBookFiles
                    .Where(f => f.AuthorId == null && f.AuthorFolder == existing.CalibreFolderName)
                    .ExecuteUpdateAsync(s => s.SetProperty(f => f.AuthorId, _ => existing.Id), ct);
            }
            return Ok(new AuthorListItem(
                existing.Id, existing.Name, existing.CalibreFolderName, existing.OpenLibraryKey,
                existing.Status.ToString(), existing.ExclusionReason,
                existing.Priority, 0, 0, 0, 0, existing.LastSyncedAt));
        }

        var name = body.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            try
            {
                var authorInfo = await _ol.FetchAuthorAsync(key, ct);
                if (string.IsNullOrWhiteSpace(authorInfo?.Name))
                {
                    ModelState.AddModelError(nameof(AddAuthorRequest.OpenLibraryKey),
                        $"OpenLibrary author '{key}' was not found.");
                    return ValidationProblem(ModelState);
                }

                name = authorInfo.Name.Trim();
            }
            catch (OpenLibraryRequestFailedException ex)
            {
                return Problem(
                    title: "OpenLibrary request failed",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }

        // Blacklist wins over manual add. If the user wants this author back
        // they must remove the blacklist entry in Settings first — otherwise
        // incoming scans would strip files off the re-added author anyway.
        var normalizedAddName = TitleNormalizer.NormalizeAuthor(name);
        if (!string.IsNullOrEmpty(normalizedAddName)
            && await _db.AuthorBlacklist.AnyAsync(b => b.NormalizedName == normalizedAddName, ct))
        {
            return Conflict(new { error = $"'{name}' is on the author blacklist. Remove them from the blacklist in Settings before re-adding." });
        }

        var author = new Author
        {
            Name = name!,
            OpenLibraryKey = key,
            Status = AuthorStatus.Pending
        };

        // If a Calibre folder name matches, link it now so the user can see that
        // association before the next sync runs.
        var normName = TitleNormalizer.NormalizeAuthor(name);
        var matchFolder = await _db.LocalBookFiles
            .Where(f => f.AuthorId == null)
            .Select(f => f.AuthorFolder)
            .Distinct()
            .ToListAsync(ct);
        author.CalibreFolderName = matchFolder
            .FirstOrDefault(f => TitleNormalizer.NormalizeAuthor(f) == normName);

        _db.Authors.Add(author);
        await _db.SaveChangesAsync(ct);

        // Adopt any orphan LocalBookFile rows whose folder matches so the new
        // author's detail page immediately lists their unmatched titles,
        // rather than waiting for the next full sync to relink them.
        if (!string.IsNullOrWhiteSpace(author.CalibreFolderName))
        {
            await _db.LocalBookFiles
                .Where(f => f.AuthorId == null && f.AuthorFolder == author.CalibreFolderName)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.AuthorId, _ => author.Id), ct);
        }

        // Name collision check: if this add creates a duplicate-name situation
        // with other unlinked authors AND every member of the group has an OL
        // key, apply the disambiguating suffix to every member's
        // CalibreFolderName so the on-disk layout stays unambiguous.
        await ApplyCollisionSuffixesAsync(author, ct);

        // Populate works immediately for newly-added authors so the detail page
        // has data before the next scheduled refresh cycle.
        try
        {
            var outcome = await _refresher.RefreshAsync(author, onMessage: null, ct);
            if (outcome.MergedIntoCanonical && outcome.CanonicalAuthorId is int canonicalId)
            {
                var canonical = await _db.Authors.FirstOrDefaultAsync(a => a.Id == canonicalId, ct);
                if (canonical is not null)
                {
                    return Ok(new AuthorListItem(
                        canonical.Id, canonical.Name, canonical.CalibreFolderName, canonical.OpenLibraryKey,
                        canonical.Status.ToString(), canonical.ExclusionReason,
                        canonical.Priority, 0, 0, 0, 0, canonical.LastSyncedAt));
                }
            }
        }
        catch (AuthorRefreshAlreadyRunningException)
        {
        }
        catch (OpenLibraryRequestFailedException ex)
        {
            _log.LogWarning(ex, "Immediate works fetch failed for newly added author {AuthorId}", author.Id);
        }

        return CreatedAtAction(nameof(Get), new { id = author.Id }, new AuthorListItem(
            author.Id, author.Name, author.CalibreFolderName, author.OpenLibraryKey,
            author.Status.ToString(), author.ExclusionReason,
            author.Priority, 0, 0, 0, 0, author.LastSyncedAt));
    }

    public sealed record AddBookRequest(
        string Title,
        int? FirstPublishYear,
        string? SeriesName,
        string? SeriesPosition,
        bool Owned);

    // Catalogues a book by hand — a work OpenLibrary doesn't list yet. It gets
    // a synthetic "XX" work key in place of an OL one; a later works-refresh
    // promotes the row in place (keeping its Book.Id) if OL picks the title
    // up. Books can only be created against an existing author.
    [HttpPost("{id:int}/books")]
    public async Task<ActionResult<AuthorDetail>> AddBook(
        int id, [FromBody] AddBookRequest body, CancellationToken ct)
    {
        var result = await _manualBooks.CreateAsync(
            id, body.Title, body.FirstPublishYear,
            body.SeriesName, body.SeriesPosition, body.Owned, ct);

        if (result.Error is not null)
            return result.Conflict
                ? Conflict(new { error = result.Error })
                : BadRequest(new { error = result.Error });

        return await Get(id, ct);
    }

    [HttpPost("{id:int}/books/openlibrary")]
    public async Task<ActionResult<AuthorDetail>> AddOpenLibraryBook(
        int id, [FromBody] AddOpenLibraryBookRequest body, CancellationToken ct)
    {
        var author = await _db.Authors.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (author is null) return NotFound(new { error = "Author not found" });
        var add = await EnsureOpenLibraryBookAsync(
            id, body.WorkKey, body.Title, body.FirstPublishYear, body.CoverId, body.Owned, ct);
        if (add.Error is not null) return BadRequest(new { error = add.Error });
        return await Get(id, ct);
    }

    [HttpPost("{id:int}/unmatched/{fileId:int}/openlibrary-match")]
    public async Task<ActionResult<AuthorDetail>> MatchLocalFileToOpenLibraryWork(
        int id, int fileId, [FromBody] MatchOpenLibraryFileRequest body, CancellationToken ct)
    {
        var currentAuthor = await _db.Authors.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (currentAuthor is null) return NotFound(new { error = "Author not found" });

        var file = await _db.LocalBookFiles.FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null) return NotFound(new { error = "Local file not found" });
        if (!FileBelongsToAuthor(file, currentAuthor))
            return BadRequest(new { error = "File does not belong to this author" });

        var targetAuthor = await ResolveTargetAuthorAsync(currentAuthor, body.PrimaryAuthorKey, body.PrimaryAuthorName, body.Authors, ct);
        if (targetAuthor is null) return BadRequest(new { error = "Could not determine the OpenLibrary author for this work" });

        var add = await EnsureOpenLibraryBookAsync(
            targetAuthor.Id, body.WorkKey, body.Title, body.FirstPublishYear, body.CoverId, owned: false, ct);
        if (add.Error is not null) return BadRequest(new { error = add.Error });

        if (targetAuthor.Id != currentAuthor.Id)
            await MoveFileToAuthorFolderAsync(file, targetAuthor, ct);
        else
        {
            file.AuthorId = targetAuthor.Id;
            if (string.IsNullOrWhiteSpace(file.AuthorFolder))
                file.AuthorFolder = targetAuthor.CalibreFolderName ?? targetAuthor.Name;
        }

        file.BookId = add.Book!.Id;
        file.ManuallyUnmatched = false;
        await _db.SaveChangesAsync(ct);
        return await Get(id, ct);
    }

    private sealed record EnsureOpenLibraryBookResult(Book? Book, string? Error);

    private async Task<EnsureOpenLibraryBookResult> EnsureOpenLibraryBookAsync(
        int authorId,
        string? rawWorkKey,
        string? rawTitle,
        int? firstPublishYear,
        int? coverId,
        bool owned,
        CancellationToken ct)
    {
        var workKey = rawWorkKey?.Trim();
        if (string.IsNullOrWhiteSpace(workKey))
            return new EnsureOpenLibraryBookResult(null, "OpenLibrary work key is required");
        if (workKey.StartsWith("/works/", StringComparison.OrdinalIgnoreCase))
            workKey = workKey[("/works/".Length)..];

        var existing = await _db.Books.FirstOrDefaultAsync(
            b => b.AuthorId == authorId && b.OpenLibraryWorkKey == workKey,
            ct);
        if (existing is not null)
        {
            if (owned && !existing.ManuallyOwned)
            {
                existing.ManuallyOwned = true;
                existing.ManuallyOwnedAt = DateTime.UtcNow;
            }
            return new EnsureOpenLibraryBookResult(existing, null);
        }

        var cleanTitle = rawTitle?.Trim();
        if (string.IsNullOrWhiteSpace(cleanTitle))
            return new EnsureOpenLibraryBookResult(null, "Title is required");

        var book = new Book
        {
            AuthorId = authorId,
            OpenLibraryWorkKey = workKey,
            Title = cleanTitle,
            NormalizedTitle = TitleNormalizer.Normalize(cleanTitle),
            FirstPublishYear = firstPublishYear,
            CoverId = coverId,
            ManuallyOwned = owned,
            ManuallyOwnedAt = owned ? DateTime.UtcNow : null,
            Subjects = "",
        };

        _db.Books.Add(book);
        await _db.SaveChangesAsync(ct);
        return new EnsureOpenLibraryBookResult(book, null);
    }

    private async Task<Author?> ResolveTargetAuthorAsync(
        Author? currentAuthor,
        string? primaryAuthorKey,
        string? primaryAuthorName,
        string? fallbackAuthors,
        CancellationToken ct)
    {
        var key = primaryAuthorKey?.Trim();
        if (string.IsNullOrWhiteSpace(key)) return currentAuthor;
        if (key.StartsWith("/authors/", StringComparison.OrdinalIgnoreCase))
            key = key[("/authors/".Length)..];

        if (currentAuthor is not null && string.Equals(currentAuthor.OpenLibraryKey, key, StringComparison.OrdinalIgnoreCase))
            return currentAuthor;

        var existing = await _db.Authors.FirstOrDefaultAsync(a => a.OpenLibraryKey == key, ct);
        if (existing is not null) return existing;

        var name = primaryAuthorName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = fallbackAuthors?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(name))
        {
            try
            {
                var fetched = await _ol.FetchAuthorAsync(key, ct);
                name = fetched?.Name?.Trim();
            }
            catch (OpenLibraryRequestFailedException)
            {
                name = null;
            }
        }

        if (string.IsNullOrWhiteSpace(name)) return null;

        var author = new Author
        {
            Name = name,
            OpenLibraryKey = key,
            Status = AuthorStatus.Pending,
        };
        _db.Authors.Add(author);
        await _db.SaveChangesAsync(ct);
        return author;
    }

    private async Task<string> MoveUntrackedPathToAuthorFolderAsync(
        string sourcePath,
        string rootPath,
        string? relativePath,
        Author targetAuthor,
        CancellationToken ct)
    {
        var targetFolder = SanitizeSegment(targetAuthor.CalibreFolderName ?? targetAuthor.Name);
        if (string.IsNullOrWhiteSpace(targetAuthor.CalibreFolderName))
            targetAuthor.CalibreFolderName = targetFolder;

        var root = rootPath.TrimEnd('\\', '/');
        var relative = NormalizeRelativePath(relativePath);
        var destPath = string.IsNullOrWhiteSpace(relative)
            ? Path.Combine(root, targetFolder, Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            : Path.Combine(root, targetFolder, relative.Replace('/', Path.DirectorySeparatorChar));

        if (System.IO.File.Exists(sourcePath))
        {
            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrWhiteSpace(destDir))
                Directory.CreateDirectory(destDir);
            var final = UniqueFilePath(destPath);
            System.IO.File.Move(sourcePath, final);
            await PruneEmptyParentsAsync(Path.GetDirectoryName(sourcePath), root, ct);
            return final;
        }

        if (Directory.Exists(sourcePath))
        {
            var destParent = Path.GetDirectoryName(destPath) ?? Path.Combine(root, targetFolder);
            Directory.CreateDirectory(destParent);
            var final = UniqueDirectoryPath(destParent, Path.GetFileName(destPath));
            Directory.Move(sourcePath, final);
            await PruneEmptyParentsAsync(Path.GetDirectoryName(sourcePath), root, ct);
            return final;
        }

        return destPath;
    }

    private static async Task PruneEmptyParentsAsync(string? startPath, string stopRoot, CancellationToken ct)
    {
        var stop = Path.GetFullPath(stopRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = startPath;
        while (!string.IsNullOrWhiteSpace(current))
        {
            ct.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!full.StartsWith(stop, StringComparison.OrdinalIgnoreCase) || string.Equals(full, stop, StringComparison.OrdinalIgnoreCase))
                break;
            if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any())
            {
                try { Directory.Delete(full); }
                catch { break; }
                current = Path.GetDirectoryName(full);
                continue;
            }
            break;
        }
        await Task.CompletedTask;
    }

    private async Task<string?> ResolveUntrackedSourcePathAsync(
        string bucket,
        string folder,
        string rootPath,
        string? relativePath,
        CancellationToken ct)
    {
        var normalizedBucket = bucket?.Trim().ToLowerInvariant();
        var customUnknown = await UnknownFolderResolver.GetCustomPathAsync(_db, ct);

        string? root = null;
        if (normalizedBucket == "unknown" && customUnknown is not null
            && string.Equals(rootPath?.Trim(), customUnknown, StringComparison.OrdinalIgnoreCase))
        {
            // The listing API returned the custom path as the rootPath sentinel.
            root = customUnknown;
        }
        else
        {
            root = (await _db.LibraryLocations
                .Where(l => l.Enabled)
                .Select(l => l.Path)
                .ToListAsync(ct))
                .FirstOrDefault(p => string.Equals(p, rootPath?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(root)) return null;
        }

        var normalizedRelative = NormalizeRelativePath(relativePath);
        var basePath = normalizedBucket switch
        {
            "unclaimed" => Path.Combine(root, folder),
            "unknown" => customUnknown is not null
                ? Path.Combine(customUnknown, folder)
                : Path.Combine(root, CalibreScanner.UnknownAuthorFolder, folder),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(basePath)) return null;

        var combined = string.IsNullOrWhiteSpace(normalizedRelative)
            ? basePath
            : Path.Combine(basePath, normalizedRelative.Replace('/', Path.DirectorySeparatorChar));
        var fullBase = Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullCombined = Path.GetFullPath(combined);
        if (!fullCombined.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase))
            return null;
        return fullCombined;
    }

    private static string? FindLibraryRootForPath(string? fullPath, IReadOnlyList<string> roots)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return null;
        return roots.FirstOrDefault(root =>
        {
            var cleanRoot = root.TrimEnd('\\', '/');
            return fullPath.StartsWith(cleanRoot, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string NormalizeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join('/', parts.Where(p => p != "." && p != ".."));
    }

    private static string CombineRelativePath(string? parent, string child)
    {
        var cleanParent = NormalizeRelativePath(parent);
        var cleanChild = NormalizeRelativePath(child);
        if (string.IsNullOrWhiteSpace(cleanParent)) return cleanChild;
        if (string.IsNullOrWhiteSpace(cleanChild)) return cleanParent;
        return $"{cleanParent}/{cleanChild}";
    }

    private static IReadOnlyList<string> FormatsInUnknownFolder(string rootPath, string folder)
    {
        var defaultUnknownPath = Path.Combine(rootPath, CalibreScanner.UnknownAuthorFolder, folder);
        var customUnknownPath = Path.Combine(rootPath, folder);
        var folderPath = Directory.Exists(defaultUnknownPath) ? defaultUnknownPath : customUnknownPath;
        if (!Directory.Exists(folderPath)) return Array.Empty<string>();
        try
        {
            return Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetExtension(p).TrimStart('.').ToLowerInvariant())
                .Where(ext => EbookExtensions.Contains("." + ext))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(ext => ext)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private async Task MoveFileToAuthorFolderAsync(LocalBookFile file, Author targetAuthor, CancellationToken ct)
    {
        var targetFolder = SanitizeSegment(targetAuthor.CalibreFolderName ?? targetAuthor.Name);
        if (string.IsNullOrWhiteSpace(targetAuthor.CalibreFolderName))
            targetAuthor.CalibreFolderName = targetFolder;

        file.AuthorId = targetAuthor.Id;
        var oldPath = file.FullPath;
        file.AuthorFolder = targetFolder;

        if (string.IsNullOrWhiteSpace(oldPath)) return;

        var locations = await _db.LibraryLocations
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);

        var location = locations.FirstOrDefault(l =>
            oldPath.StartsWith(l.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
        if (location is null) return;

        var libRoot = location.TrimEnd('\\', '/');
        var relative = oldPath[libRoot.Length..].TrimStart('\\', '/');
        var firstSep = relative.IndexOfAny(new[] { '\\', '/' });
        if (firstSep < 0) return;

        var remainder = relative[(firstSep + 1)..];
        var destPath = Path.Combine(libRoot, targetFolder, remainder);

        if (System.IO.File.Exists(oldPath))
        {
            var destDir = Path.GetDirectoryName(destPath);
            if (destDir is not null) Directory.CreateDirectory(destDir);
            var final = UniqueFilePath(destPath);
            System.IO.File.Move(oldPath, final);
            file.FullPath = final;
        }
        else if (Directory.Exists(oldPath))
        {
            var destParent = Path.GetDirectoryName(destPath);
            if (destParent is not null) Directory.CreateDirectory(destParent);
            var final = UniqueDirectoryPath(destParent ?? libRoot, Path.GetFileName(destPath));
            Directory.Move(oldPath, final);
            file.FullPath = final;
        }
        else
        {
            file.FullPath = destPath;
        }

        var oldDir = Directory.Exists(oldPath) ? Path.GetDirectoryName(oldPath) : Path.GetDirectoryName(oldPath);
        if (!string.IsNullOrWhiteSpace(oldDir)
            && Directory.Exists(oldDir)
            && !Directory.EnumerateFileSystemEntries(oldDir).Any())
        {
            try { Directory.Delete(oldDir); } catch { }
        }
    }

    private static string UniqueFilePath(string desired)
    {
        if (!System.IO.File.Exists(desired) && !Directory.Exists(desired)) return desired;
        var dir = Path.GetDirectoryName(desired) ?? "";
        var stem = Path.GetFileNameWithoutExtension(desired);
        var ext = Path.GetExtension(desired);
        for (var i = 2; i < 1000; i++)
        {
            var next = Path.Combine(dir, $"{stem}_{i}{ext}");
            if (!System.IO.File.Exists(next) && !Directory.Exists(next)) return next;
        }
        return Path.Combine(dir, $"{stem}_{DateTime.UtcNow:yyyyMMddHHmmss}{ext}");
    }

    private static string UniqueDirectoryPath(string parent, string leaf)
    {
        var candidate = Path.Combine(parent, leaf);
        if (!Directory.Exists(candidate) && !System.IO.File.Exists(candidate)) return candidate;
        for (var i = 2; i < 1000; i++)
        {
            var next = Path.Combine(parent, $"{leaf} ({i})");
            if (!Directory.Exists(next) && !System.IO.File.Exists(next)) return next;
        }
        return Path.Combine(parent, $"{leaf} ({DateTime.UtcNow:yyyyMMddHHmmss})");
    }

    // Updates CalibreFolderName for every member of `author`'s collision group
    // to the disambiguated form when AuthorFolderNameResolver says one is due.
    // Folders on disk are NOT renamed here — use the disambiguate-folders
    // endpoint for that. This call only keeps the DB metadata consistent so
    // future scans and the migration endpoint can act on a coherent baseline.
    private async Task ApplyCollisionSuffixesAsync(Author author, CancellationToken ct)
    {
        var all = await _db.Authors.ToListAsync(ct);
        var group = AuthorFolderNameResolver.FindCollisionGroup(author, all);
        if (group.Count < 2) return;

        bool any = false;
        foreach (var member in group)
        {
            var target = AuthorFolderNameResolver.Resolve(member, all);
            if (!string.Equals(member.CalibreFolderName, target, StringComparison.Ordinal))
            {
                member.CalibreFolderName = target;
                any = true;
            }
        }
        if (any) await _db.SaveChangesAsync(ct);
    }

    public sealed record SetPriorityRequest(int Priority);

    // Update the user's 0–5 star rating. Values outside the range are
    // rejected so the UI can't store garbage.
    [HttpPut("{id:int}/priority")]
    public async Task<IActionResult> SetPriority(int id, [FromBody] SetPriorityRequest body, CancellationToken ct)
    {
        if (body.Priority < 0 || body.Priority > 5)
            return BadRequest(new { error = "Priority must be 0–5" });
        var author = await _db.Authors.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (author is null) return NotFound();
        author.Priority = body.Priority;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    public sealed record SetRefreshIntervalRequest(int? Days);

    // Sets a fixed works-refresh cadence for this author. When Days is null the
    // calculated interval (based on most-recent publication year) is restored.
    // Validated to 1–3650 so the column never holds zero or absurdly large values.
    [HttpPut("{id:int}/refresh-interval")]
    public async Task<IActionResult> SetRefreshInterval(int id, [FromBody] SetRefreshIntervalRequest body, CancellationToken ct)
    {
        if (body.Days.HasValue && (body.Days.Value < 1 || body.Days.Value > 3650))
            return BadRequest(new { error = "Days must be between 1 and 3650" });
        var author = await _db.Authors.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (author is null) return NotFound();
        author.RefreshIntervalDays = body.Days;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    public sealed record SaveNotesRequest(string? Notes);

    [HttpPut("{id:int}/notes")]
    public async Task<IActionResult> SaveNotes(int id, [FromBody] SaveNotesRequest body, CancellationToken ct)
    {
        var author = await _db.Authors.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (author is null) return NotFound();
        author.Notes = string.IsNullOrWhiteSpace(body.Notes) ? null : body.Notes.Trim();
        await _db.SaveChangesAsync(ct);
        return Ok(new { author.Id, author.Notes });
    }

    public sealed record SetNotifyOnNewBooksRequest(bool Enabled);

    // Per-author toggle for Pushover new-book alerts. Requires Pushover
    // credentials in AppSettings for any notification to actually fire.
    [HttpPut("{id:int}/notify-new-books")]
    public async Task<IActionResult> SetNotifyOnNewBooks(int id, [FromBody] SetNotifyOnNewBooksRequest body, CancellationToken ct)
    {
        var author = await _db.Authors.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (author is null) return NotFound();
        author.NotifyOnNewBooks = body.Enabled;
        await _db.SaveChangesAsync(ct);
        return Ok(new { author.Id, author.NotifyOnNewBooks });
    }

    public sealed record LinkAuthorRequest(int CanonicalAuthorId, bool IsPenName);

    // Marks `id` as a duplicate (or pen name) of `CanonicalAuthorId`. When
    // IsPenName == false, every LocalBookFile of the child author is relocated
    // on disk to live under the canonical's author folder; when IsPenName ==
    // true the books and files stay where they are and we only record the link.
    [HttpPut("{id:int}/link")]
    public async Task<ActionResult<AuthorDetail>> Link(int id, [FromBody] LinkAuthorRequest body, CancellationToken ct)
    {
        if (body.CanonicalAuthorId == id)
            return BadRequest(new { error = "An author cannot be linked to itself" });

        var child = await _db.Authors.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (child is null) return NotFound();

        var canonical = await _db.Authors.FirstOrDefaultAsync(a => a.Id == body.CanonicalAuthorId, ct);
        if (canonical is null) return BadRequest(new { error = "Canonical author not found" });

        // Disallow chains: the canonical must itself be a top-level entry, and
        // the child must not already be a canonical for other rows.
        if (canonical.LinkedToAuthorId is not null)
            return BadRequest(new { error = "Target author is already linked to another — link to that canonical instead" });
        if (await _db.Authors.AnyAsync(a => a.LinkedToAuthorId == id, ct))
            return BadRequest(new { error = "This author has its own linked children — unlink them first" });

        child.LinkedToAuthorId = canonical.Id;
        child.IsPenName = body.IsPenName;

        // Relocate files only on the duplicate (non-pen-name) path. Pen names
        // keep their own folder on disk.
        if (!body.IsPenName)
            await RelocateChildFilesAsync(child, canonical, ct);

        await _db.SaveChangesAsync(ct);
        return await Get(id, ct);
    }

    [HttpDelete("{id:int}/link")]
    public async Task<ActionResult<AuthorDetail>> Unlink(int id, CancellationToken ct)
    {
        var child = await _db.Authors.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (child is null) return NotFound();

        // Files stay wherever they currently are — undoing a link does not
        // automatically move them back to the old child folder. The user can
        // re-link or move files manually if they need a different layout.
        child.LinkedToAuthorId = null;
        child.IsPenName = false;
        await _db.SaveChangesAsync(ct);
        return await Get(id, ct);
    }

    // Moves every LocalBookFile owned by `child` from its on-disk folder under
    // the child author's name into the equivalent path under the canonical's
    // author folder, keeping any series subfolder structure intact. Updates the
    // DB record (AuthorId, AuthorFolder, FullPath) row-by-row so a crashed move
    // partway through leaves a consistent half-state. Files outside any known
    // library location are left untouched.
    private async Task RelocateChildFilesAsync(
        Author child, Author canonical, CancellationToken ct)
    {
        var locations = await _db.LibraryLocations
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);
        if (locations.Count == 0) return;

        var canonicalFolder = SanitizeSegment(
            canonical.CalibreFolderName ?? canonical.Name);
        var childFolderNames = FolderCandidatesFor(child);

        var files = await _db.LocalBookFiles
            .Where(f => f.AuthorId == child.Id)
            .ToListAsync(ct);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(file.FullPath))
            {
                file.AuthorId = canonical.Id;
                file.AuthorFolder = canonicalFolder;
                continue;
            }

            // Find the library location this file lives under so we can map
            // {root}/{childFolder}/rest → {root}/{canonicalFolder}/rest.
            var location = locations.FirstOrDefault(l =>
                file.FullPath.StartsWith(l.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
            if (location is null)
            {
                // Path is outside any enabled location — just update metadata
                // and skip the disk move.
                file.AuthorId = canonical.Id;
                file.AuthorFolder = canonicalFolder;
                continue;
            }

            var libRoot = location.TrimEnd('\\', '/');
            var relative = file.FullPath[libRoot.Length..].TrimStart('\\', '/');
            var firstSep = relative.IndexOfAny(new[] { '\\', '/' });
            string remainder;
            if (firstSep < 0)
            {
                // Bare filename at the library root — nothing useful to do.
                file.AuthorId = canonical.Id;
                file.AuthorFolder = canonicalFolder;
                continue;
            }
            var existingAuthorFolder = relative[..firstSep];
            // Only relocate if the file is actually inside one of the child's
            // expected folders. Skip otherwise so we don't move random files.
            if (!childFolderNames.Contains(existingAuthorFolder, StringComparer.OrdinalIgnoreCase))
            {
                file.AuthorId = canonical.Id;
                file.AuthorFolder = canonicalFolder;
                continue;
            }

            remainder = relative[(firstSep + 1)..];
            var destPath = Path.Combine(libRoot, canonicalFolder, remainder);
            var destDir = Path.GetDirectoryName(destPath);

            try
            {
                if (destDir is not null) Directory.CreateDirectory(destDir);

                if (System.IO.File.Exists(file.FullPath))
                {
                    var finalDest = UniqueFile(destPath);
                    System.IO.File.Move(file.FullPath, finalDest);
                    file.FullPath = finalDest;
                }
                else if (Directory.Exists(file.FullPath))
                {
                    var finalDest = UniqueDirectory(Path.GetDirectoryName(destPath)!, Path.GetFileName(destPath));
                    Directory.Move(file.FullPath, finalDest);
                    file.FullPath = finalDest;
                }
                // If the source has already disappeared, just update the DB
                // pointer and move on.
                else
                {
                    file.FullPath = destPath;
                }
            }
            catch (IOException)
            {
                // Best-effort move — leave the DB row in sync with whatever we
                // managed to do above and let the next SyncService run re-detect
                // the actual on-disk location.
            }

            file.AuthorId = canonical.Id;
            file.AuthorFolder = canonicalFolder;
        }

        // Prune the now-empty child author folder(s) so they don't linger as
        // ghost directories.
        foreach (var libRoot in locations.Select(l => l.TrimEnd('\\', '/')))
            foreach (var name in childFolderNames)
            {
                var dir = Path.Combine(libRoot, name);
                try
                {
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch { /* best effort */ }
            }
    }

    // Picks the first non-existing variant of a file path by suffixing "_N"
    // before the extension. Mirrors UniqueDirectory but for files.
    private static string UniqueFile(string desired)
    {
        if (!System.IO.File.Exists(desired) && !Directory.Exists(desired)) return desired;
        var dir = Path.GetDirectoryName(desired) ?? "";
        var stem = Path.GetFileNameWithoutExtension(desired);
        var ext  = Path.GetExtension(desired);
        for (var i = 2; i < 1000; i++)
        {
            var next = Path.Combine(dir, $"{stem}_{i}{ext}");
            if (!System.IO.File.Exists(next) && !Directory.Exists(next)) return next;
        }
        return Path.Combine(dir, $"{stem}_{DateTime.UtcNow:yyyyMMddHHmmss}{ext}");
    }

    // On-demand refresh of a single author: resolves the OL key if missing,
    // fetches works, reapplies exclusion rules, and reschedules. This runs
    // independently of the Hangfire-coordinated jobs; only same-author refreshes
    // are serialized.
    [HttpPost("{id:int}/refresh")]
    public async Task<ActionResult<AuthorDetail>> Refresh(int id, CancellationToken ct)
    {
        var author = await _db.Authors.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (author is null) return NotFound();

        try
        {
            await _refresher.RefreshAsync(author, onMessage: null, ct);
        }
        catch (AuthorRefreshAlreadyRunningException)
        {
            return Conflict(new { error = $"Author '{author.Name}' is already being refreshed." });
        }
        catch (OpenLibraryRequestFailedException ex)
        {
            return Problem(
                title: "OpenLibrary request failed",
                detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return await Get(id, ct);
    }

    // Deleting an author is destructive: every local file linked to them is
    // moved off the library and back into the incoming bucket (grouped under
    // <incoming>/<AuthorName>/<TitleFolder>/), DB rows are removed, the
    // author row is deleted, and the author's normalized name goes on the
    // blacklist so subsequent scans don't silently re-add them.
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Remove(int id, CancellationToken ct)
    {
        var author = await _db.Authors.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (author is null) return NotFound();

        var incomingSetting = await _db.AppSettings
            .FirstOrDefaultAsync(s => s.Key == AppSettingKeys.IncomingFolder, ct);
        var incomingPath = incomingSetting?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(incomingPath))
            return BadRequest(new { error = "Incoming folder is not configured — set it in Settings before removing authors." });
        if (!Directory.Exists(incomingPath))
            return BadRequest(new { error = $"Incoming folder does not exist: {incomingPath}" });

        var folderCandidates = FolderCandidatesFor(author);
        var files = await _db.LocalBookFiles
            .Where(f => f.AuthorId == id
                || (f.AuthorId == null && folderCandidates.Contains(f.AuthorFolder)))
            .ToListAsync(ct);

        // All the author's files go under a single per-author subfolder of
        // incoming so bulk re-imports stay organised and multi-format books
        // stay grouped with their siblings.
        var authorDestRoot = UniqueDirectory(incomingPath, author.Name);
        try { Directory.CreateDirectory(authorDestRoot); }
        catch (IOException ex)
        {
            return StatusCode(500, new { error = $"Could not create destination folder: {ex.Message}" });
        }

        var movedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var moveWarnings = new List<string>();

        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.FullPath)) continue;
            var src = file.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(src)) continue;
            if (!movedSources.Add(src)) continue; // sibling row pointing at the same folder

            var leaf = !string.IsNullOrWhiteSpace(file.TitleFolder) ? file.TitleFolder : Path.GetFileName(src);
            if (string.IsNullOrWhiteSpace(leaf)) leaf = $"returned-{file.Id}";

            var dest = UniqueDirectory(authorDestRoot, leaf);
            try
            {
                Directory.Move(src, dest);
            }
            catch (IOException ex)
            {
                moveWarnings.Add($"{leaf}: {ex.Message}");
            }
        }

        // If the author's Calibre author-level folders are now empty, prune
        // them so the library view isn't left with ghost directories.
        var parentDirs = files
            .Where(f => !string.IsNullOrWhiteSpace(f.FullPath))
            .Select(f => Path.GetDirectoryName(f.FullPath!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var parent in parentDirs)
        {
            try
            {
                if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent!).Any())
                    Directory.Delete(parent!);
            }
            catch { /* best effort */ }
        }

        // Drop every local-file row we collected. Books cascade via the
        // Author FK when the author row is removed below.
        _db.LocalBookFiles.RemoveRange(files);

        var normalizedName = TitleNormalizer.NormalizeAuthor(author.Name);
        if (!string.IsNullOrEmpty(normalizedName)
            && !await _db.AuthorBlacklist.AnyAsync(b => b.NormalizedName == normalizedName, ct))
        {
            _db.AuthorBlacklist.Add(new AuthorBlacklist
            {
                Name = author.Name,
                NormalizedName = normalizedName,
                FolderName = author.CalibreFolderName,
                AddedAt = DateTime.UtcNow,
                Reason = "Removed from watchlist"
            });
        }

        _db.Authors.Remove(author);
        await _db.SaveChangesAsync(ct);

        if (moveWarnings.Count > 0)
            return Ok(new { warnings = moveWarnings });

        return NoContent();
    }

    public sealed record UnclaimedFolder(string AuthorFolder, int FileCount, IReadOnlyList<string> RootPaths, IReadOnlyList<string> Formats);

    public sealed record UntrackedFolderEntry(
        string Name,
        string RelativePath,
        bool IsDirectory,
        string SearchQuery);

    public sealed record UntrackedFolderContents(
        string Bucket,
        string Folder,
        string RootPath,
        string CurrentPath,
        string? ParentPath,
        bool RecursiveFilesOnly,
        IReadOnlyList<UntrackedFolderEntry> Entries);

    public sealed record MatchUntrackedOpenLibraryRequest(
        string Bucket,
        string Folder,
        string RootPath,
        string? RelativePath,
        string? WorkKey,
        string? Title,
        int? FirstPublishYear,
        int? CoverId,
        string? Authors,
        string? PrimaryAuthorKey,
        string? PrimaryAuthorName);

    // Calibre author folders that don't match any tracked author.
    [HttpGet("~/api/unclaimed")]
    public async Task<IReadOnlyList<UnclaimedFolder>> Unclaimed(CancellationToken ct)
    {
        var locations = await _db.LibraryLocations
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);

        var rows = await _db.LocalBookFiles.AsNoTracking()
            .Where(f => f.AuthorId == null)
            .ToListAsync(ct);

        return rows
            .GroupBy(f => f.AuthorFolder)
            .OrderBy(g => g.Key)
            .Select(g => new UnclaimedFolder(
                g.Key,
                g.Count(),
                g.Select(f => FindLibraryRootForPath(f.FullPath, locations))
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Cast<string>()
                    .ToList(),
                g.SelectMany(f => FormatsInFolder(f.FullPath))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x)
                    .ToList()))
            .ToList();
    }

    [HttpGet("~/api/untracked/contents")]
    public async Task<ActionResult<UntrackedFolderContents>> GetUntrackedContents(
        [FromQuery] string bucket,
        [FromQuery] string folder,
        [FromQuery] string rootPath,
        [FromQuery] string? path,
        [FromQuery] bool recursiveFilesOnly,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(rootPath))
            return BadRequest(new { error = "bucket, folder, and rootPath are required" });

        var sourcePath = await ResolveUntrackedSourcePathAsync(bucket, folder, rootPath, path, ct);
        if (sourcePath is null)
            return NotFound(new { error = "Folder not found" });
        if (!Directory.Exists(sourcePath))
            return BadRequest(new { error = "Only folders can be drilled into" });

        var relativePath = NormalizeRelativePath(path);
        var parentPath = string.IsNullOrWhiteSpace(relativePath)
            ? null
            : NormalizeRelativePath(Path.GetDirectoryName(relativePath)?.Replace('\\', '/'));

        List<UntrackedFolderEntry> entries;
        if (recursiveFilesOnly)
        {
            entries = Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories)
                .Where(p => CalibreScanner.EbookExtensions.Contains(Path.GetExtension(p))
                            || CalibreScanner.ArchiveExtensions.Contains(Path.GetExtension(p)))
                .Select(p => Path.GetRelativePath(sourcePath, p).Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => new UntrackedFolderEntry(
                    Path.GetFileName(p),
                    CombineRelativePath(relativePath, p),
                    false,
                    Path.GetFileNameWithoutExtension(p)))
                .ToList();
        }
        else
        {
            entries = Directory.EnumerateFileSystemEntries(sourcePath)
                .Where(p => Directory.Exists(p)
                            || CalibreScanner.EbookExtensions.Contains(Path.GetExtension(p))
                            || CalibreScanner.ArchiveExtensions.Contains(Path.GetExtension(p)))
                .Select(p => new
                {
                    Path = p,
                    Name = Path.GetFileName(p),
                    IsDirectory = Directory.Exists(p)
                })
                .OrderByDescending(x => x.IsDirectory)
                .ThenBy(x => x.Name)
                .Select(x => new UntrackedFolderEntry(
                    x.Name,
                    CombineRelativePath(relativePath, x.Name),
                    x.IsDirectory,
                    x.IsDirectory ? x.Name : Path.GetFileNameWithoutExtension(x.Name)))
                .ToList();
        }

        return Ok(new UntrackedFolderContents(bucket.Trim(), folder.Trim(), rootPath.Trim(), relativePath, parentPath, recursiveFilesOnly, entries));
    }

    // In-browser preview for files that live under unclaimed/__unknown. The
    // file isn't in LocalBookFiles yet, so the regular /api/files/{id}/preview
    // endpoint won't find it — this one resolves via the same untracked path
    // resolver and streams the bytes with an `inline` disposition.
    [HttpGet("~/api/untracked/preview")]
    public async Task<IActionResult> PreviewUntracked(
        [FromQuery] string bucket,
        [FromQuery] string folder,
        [FromQuery] string rootPath,
        [FromQuery] string? path,
        [FromQuery] string format,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(format))
            return BadRequest(new { error = "format query parameter is required" });

        var sourcePath = await ResolveUntrackedSourcePathAsync(bucket, folder, rootPath, path, ct);
        if (sourcePath is null) return NotFound(new { error = "Selected path not found" });

        if (!System.IO.File.Exists(sourcePath))
            return NotFound(new { error = "Only files can be previewed" });

        var ext = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
        if (!string.Equals(ext, format, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = $"File extension '.{ext}' does not match requested format '.{format}'" });

        if (!Services.Sync.FilePreviewResolver.SupportedFormats.TryGetValue(format, out var contentType))
            return StatusCode(415, new { error = $"Preview not supported for '.{format}'. Supported: epub, pdf, txt." });

        // Build the allowed-roots list: every enabled library location PLUS
        // the custom __unknown path when one is set (it may live outside the
        // library locations entirely).
        var roots = await _db.LibraryLocations.AsNoTracking()
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);
        var customUnknown = await UnknownFolderResolver.GetCustomPathAsync(_db, ct);
        var allowedRoots = customUnknown is null
            ? (IReadOnlyList<string>)roots
            : roots.Append(customUnknown).ToList();

        if (!Services.Sync.FilePreviewResolver.IsInsideAnyRoot(sourcePath, allowedRoots))
            return StatusCode(403, new { error = "Refusing to serve a file outside enabled library locations" });

        var safeName = Path.GetFileName(sourcePath).Replace("\"", "");
        Response.Headers["Content-Disposition"] = $"inline; filename=\"{safeName}\"";
        return PhysicalFile(sourcePath, contentType, enableRangeProcessing: true);
    }

    [HttpPost("~/api/untracked/match-openlibrary")]
    public async Task<ActionResult<object>> MatchUntrackedToOpenLibrary(
        [FromBody] MatchUntrackedOpenLibraryRequest body,
        CancellationToken ct)
    {
        var sourcePath = await ResolveUntrackedSourcePathAsync(body.Bucket, body.Folder, body.RootPath, body.RelativePath, ct);
        if (sourcePath is null)
            return NotFound(new { error = "Selected path not found" });

        var targetAuthor = await ResolveTargetAuthorAsync(
            null,
            body.PrimaryAuthorKey,
            body.PrimaryAuthorName,
            body.Authors,
            ct);
        if (targetAuthor is null)
            return BadRequest(new { error = "Could not determine the OpenLibrary author for this work" });

        var add = await EnsureOpenLibraryBookAsync(
            targetAuthor.Id,
            body.WorkKey,
            body.Title,
            body.FirstPublishYear,
            body.CoverId,
            owned: false,
            ct);
        if (add.Error is not null)
            return BadRequest(new { error = add.Error });

        var existing = await _db.LocalBookFiles.FirstOrDefaultAsync(f => f.FullPath == sourcePath, ct);
        var file = existing ?? new LocalBookFile();
        if (existing is null)
            _db.LocalBookFiles.Add(file);

        var finalPath = await MoveUntrackedPathToAuthorFolderAsync(
            sourcePath,
            body.RootPath,
            NormalizeRelativePath(body.RelativePath),
            targetAuthor,
            ct);

        file.AuthorId = targetAuthor.Id;
        file.BookId = add.Book!.Id;
        file.ManuallyUnmatched = false;
        file.AuthorFolder = targetAuthor.CalibreFolderName ?? targetAuthor.Name;
        file.TitleFolder = Directory.Exists(finalPath)
            ? Path.GetFileName(finalPath)
            : Path.GetFileNameWithoutExtension(finalPath);
        file.FullPath = finalPath;
        file.NormalizedTitle = TitleNormalizer.Normalize(file.TitleFolder);

        await _db.SaveChangesAsync(ct);
        return Ok(new { authorId = targetAuthor.Id, bookId = add.Book.Id, fullPath = finalPath });
    }

    [HttpDelete("~/api/untracked")]
    public async Task<IActionResult> DeleteUntrackedPath(
        [FromQuery] string bucket,
        [FromQuery] string folder,
        [FromQuery] string rootPath,
        [FromQuery] string? path,
        CancellationToken ct)
    {
        var sourcePath = await ResolveUntrackedSourcePathAsync(bucket, folder, rootPath, path, ct);
        if (sourcePath is null)
            return NotFound(new { error = "Selected path not found" });

        var isDirectory = _fs.DirectoryExists(sourcePath);
        var isFile = _fs.FileExists(sourcePath);
        if (!isDirectory && !isFile)
            return NotFound(new { error = "Selected path no longer exists on disk" });

        if (isDirectory)
            _fs.DeleteDirectory(sourcePath, recursive: true);
        else
            _fs.DeleteFile(sourcePath);

        var normalizedSource = sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var directParent = Path.GetDirectoryName(normalizedSource);
        var prefix1 = normalizedSource + Path.DirectorySeparatorChar;
        var prefix2 = normalizedSource + Path.AltDirectorySeparatorChar;

        var staleRows = await _db.LocalBookFiles
            .Where(f => f.FullPath == normalizedSource
                     || (isDirectory && (f.FullPath.StartsWith(prefix1) || f.FullPath.StartsWith(prefix2)))
                     || (!isDirectory && directParent != null && f.FullPath == directParent))
            .ToListAsync(ct);
        if (staleRows.Count > 0)
            _db.LocalBookFiles.RemoveRange(staleRows);

        var bucketRoot = await ResolveUntrackedSourcePathAsync(bucket, folder, rootPath, null, ct);
        if (!string.IsNullOrWhiteSpace(bucketRoot))
            await PruneEmptyParentsAsync(Path.GetDirectoryName(normalizedSource), bucketRoot, ct);

        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // Moves all files for an untracked folder back to incoming, deletes the
    // (now-empty) author folder from the library, and removes the DB rows.
    [HttpDelete("~/api/unclaimed")]
    public async Task<IActionResult> DiscardUnclaimed([FromQuery] string folder, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return BadRequest(new { error = "folder is required" });

        var incomingSetting = await _db.AppSettings
            .FirstOrDefaultAsync(s => s.Key == AppSettingKeys.IncomingFolder, ct);
        var incomingPath = incomingSetting?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(incomingPath))
            return BadRequest(new { error = "Incoming folder is not configured — set it in Settings first." });
        if (!Directory.Exists(incomingPath))
            return BadRequest(new { error = $"Incoming folder does not exist: {incomingPath}" });

        var files = await _db.LocalBookFiles
            .Where(f => f.AuthorFolder == folder && f.AuthorId == null)
            .ToListAsync(ct);

        if (files.Count == 0)
            return NotFound(new { error = $"No untracked files found for folder '{folder}'" });

        var authorDestRoot = UniqueDirectory(incomingPath, folder);
        try { Directory.CreateDirectory(authorDestRoot); }
        catch (IOException ex)
        {
            return StatusCode(500, new { error = $"Could not create destination folder: {ex.Message}" });
        }

        var movedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var moveWarnings = new List<string>();
        string? authorDirOnDisk = null;

        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.FullPath)) continue;
            var src = file.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(src)) continue;
            if (!movedSources.Add(src)) continue;

            authorDirOnDisk ??= Path.GetDirectoryName(src);

            var leaf = !string.IsNullOrWhiteSpace(file.TitleFolder) ? file.TitleFolder : Path.GetFileName(src);
            if (string.IsNullOrWhiteSpace(leaf)) leaf = $"returned-{file.Id}";

            var dest = UniqueDirectory(authorDestRoot, leaf);
            try { Directory.Move(src, dest); }
            catch (IOException ex) { moveWarnings.Add($"{leaf}: {ex.Message}"); }
        }

        // Prune the now-empty author folder from the library.
        if (!string.IsNullOrWhiteSpace(authorDirOnDisk)
            && Directory.Exists(authorDirOnDisk)
            && !Directory.EnumerateFileSystemEntries(authorDirOnDisk).Any())
        {
            try { Directory.Delete(authorDirOnDisk); } catch { /* best effort */ }
        }

        // If nothing actually moved, clean up the empty dest root we created.
        if (Directory.Exists(authorDestRoot)
            && !Directory.EnumerateFileSystemEntries(authorDestRoot).Any())
        {
            try { Directory.Delete(authorDestRoot); } catch { /* best effort */ }
        }

        _db.LocalBookFiles.RemoveRange(files);

        var normalizedFolder = TitleNormalizer.NormalizeAuthor(folder);
        if (!string.IsNullOrEmpty(normalizedFolder)
            && !await _db.AuthorBlacklist.AnyAsync(b => b.NormalizedName == normalizedFolder, ct))
        {
            _db.AuthorBlacklist.Add(new AuthorBlacklist
            {
                Name = folder,
                NormalizedName = normalizedFolder,
                FolderName = folder,
                AddedAt = DateTime.UtcNow,
                Reason = "Discarded untracked folder"
            });
        }

        await _db.SaveChangesAsync(ct);

        if (moveWarnings.Count > 0)
            return Ok(new { warnings = moveWarnings });

        return NoContent();
    }

    // Moves every untracked file back to incoming in one shot.
    // Unlike the per-folder DELETE, this does NOT blacklist — the intent is
    // a bulk reset so the incoming processor can re-evaluate everything.
    [HttpDelete("~/api/unclaimed/all")]
    public async Task<IActionResult> DiscardAllUnclaimed(CancellationToken ct)
    {
        var incomingSetting = await _db.AppSettings
            .FirstOrDefaultAsync(s => s.Key == AppSettingKeys.IncomingFolder, ct);
        var incomingPath = incomingSetting?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(incomingPath))
            return BadRequest(new { error = "Incoming folder is not configured — set it in Settings first." });
        if (!Directory.Exists(incomingPath))
            return BadRequest(new { error = $"Incoming folder does not exist: {incomingPath}" });

        var allFiles = await _db.LocalBookFiles
            .Where(f => f.AuthorId == null)
            .ToListAsync(ct);

        if (allFiles.Count == 0)
            return NoContent();

        var moveWarnings = new List<string>();

        foreach (var group in allFiles.GroupBy(f => f.AuthorFolder))
        {
            var folder = group.Key;
            var authorDestRoot = UniqueDirectory(incomingPath, folder);
            try { Directory.CreateDirectory(authorDestRoot); }
            catch (IOException ex)
            {
                moveWarnings.Add($"{folder}: Could not create destination — {ex.Message}");
                continue;
            }

            var movedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? authorDirOnDisk = null;

            foreach (var file in group)
            {
                if (string.IsNullOrWhiteSpace(file.FullPath)) continue;
                var src = file.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!Directory.Exists(src)) continue;
                if (!movedSources.Add(src)) continue;

                authorDirOnDisk ??= Path.GetDirectoryName(src);

                var leaf = !string.IsNullOrWhiteSpace(file.TitleFolder) ? file.TitleFolder : Path.GetFileName(src);
                if (string.IsNullOrWhiteSpace(leaf)) leaf = $"returned-{file.Id}";

                var dest = UniqueDirectory(authorDestRoot, leaf);
                try { Directory.Move(src, dest); }
                catch (IOException ex) { moveWarnings.Add($"{leaf}: {ex.Message}"); }
            }

            if (!string.IsNullOrWhiteSpace(authorDirOnDisk)
                && Directory.Exists(authorDirOnDisk)
                && !Directory.EnumerateFileSystemEntries(authorDirOnDisk).Any())
            {
                try { Directory.Delete(authorDirOnDisk); } catch { /* best effort */ }
            }

            if (Directory.Exists(authorDestRoot)
                && !Directory.EnumerateFileSystemEntries(authorDestRoot).Any())
            {
                try { Directory.Delete(authorDestRoot); } catch { /* best effort */ }
            }
        }

        _db.LocalBookFiles.RemoveRange(allFiles);
        await _db.SaveChangesAsync(ct);

        if (moveWarnings.Count > 0)
            return Ok(new { warnings = moveWarnings });

        return NoContent();
    }

    public sealed record UnknownFolder(string AuthorFolder, int FileCount, IReadOnlyList<string> RootPaths, IReadOnlyList<string> Formats);

    // Lists author-level folders that exist inside the __unknown quarantine
    // bucket across all enabled library locations.
    [HttpGet("~/api/unknown-folders")]
    public async Task<IReadOnlyList<UnknownFolder>> ListUnknownFolders(CancellationToken ct)
    {
        var locations = await _db.LibraryLocations
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);

        var customUnknown = await UnknownFolderResolver.GetCustomPathAsync(_db, ct);
        // With a custom path, every folder reports it as the RootPath sentinel
        // — that's what the client passes back on delete/match actions.
        var scanRoots = customUnknown is not null
            ? new[] { (UnknownRoot: customUnknown, RootPath: customUnknown) }
            : locations.Select(l => (UnknownRoot: Path.Combine(l, CalibreScanner.UnknownAuthorFolder), RootPath: l)).ToArray();

        var result = new List<(string Folder, int Count, string RootPath)>();
        foreach (var (unknownRoot, rootPath) in scanRoots)
        {
            if (!Directory.Exists(unknownRoot)) continue;
            foreach (var dir in Directory.GetDirectories(unknownRoot))
            {
                var fileCount = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;
                if (fileCount > 0)
                    result.Add((Folder: Path.GetFileName(dir), Count: fileCount, RootPath: rootPath));
            }
        }

        return result
            .GroupBy(r => r.Folder)
            .OrderBy(g => g.Key)
            .Select(g => new UnknownFolder(
                g.Key,
                g.Sum(x => x.Count),
                g.Select(x => x.RootPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                g.SelectMany(x => FormatsInUnknownFolder(x.RootPath, x.Folder))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x)
                    .ToList()))
            .ToList();
    }

    // Moves a single __unknown author folder back to the incoming bucket so it
    // can be re-evaluated after the user adds the author to the watchlist.
    [HttpDelete("~/api/unknown-folders")]
    public async Task<IActionResult> ReturnUnknownFolder([FromQuery] string folder, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return BadRequest(new { error = "folder is required" });

        var incomingSetting = await _db.AppSettings
            .FirstOrDefaultAsync(s => s.Key == AppSettingKeys.IncomingFolder, ct);
        var incomingPath = incomingSetting?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(incomingPath))
            return BadRequest(new { error = "Incoming folder is not configured — set it in Settings first." });
        if (!Directory.Exists(incomingPath))
            return BadRequest(new { error = $"Incoming folder does not exist: {incomingPath}" });

        var locations = await _db.LibraryLocations
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);

        var unknownRoots = await UnknownFolderResolver.GetSourceRootsAsync(_db, locations, ct);

        var warnings = new List<string>();
        bool found = false;
        foreach (var unknownRoot in unknownRoots)
        {
            var src = Path.Combine(unknownRoot, folder);
            if (!Directory.Exists(src)) continue;
            found = true;
            var dest = UniqueDirectory(incomingPath, folder);
            try
            {
                Directory.CreateDirectory(dest);
                foreach (var entry in Directory.GetFileSystemEntries(src))
                {
                    var name = Path.GetFileName(entry);
                    var target = Path.Combine(dest, name);
                    if (Directory.Exists(entry)) Directory.Move(entry, target);
                    else System.IO.File.Move(entry, target, overwrite: false);
                }
                if (!Directory.EnumerateFileSystemEntries(src).Any())
                    Directory.Delete(src);
            }
            catch (IOException ex) { warnings.Add(ex.Message); }
        }

        if (!found)
            return NotFound(new { error = $"Folder '{folder}' not found in __unknown" });

        if (warnings.Count > 0)
            return Ok(new { warnings });

        return NoContent();
    }

    // Moves ALL __unknown author folders back to incoming in one shot.
    [HttpDelete("~/api/unknown-folders/all")]
    public async Task<IActionResult> ReturnAllUnknownFolders(CancellationToken ct)
    {
        var incomingSetting = await _db.AppSettings
            .FirstOrDefaultAsync(s => s.Key == AppSettingKeys.IncomingFolder, ct);
        var incomingPath = incomingSetting?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(incomingPath))
            return BadRequest(new { error = "Incoming folder is not configured — set it in Settings first." });
        if (!Directory.Exists(incomingPath))
            return BadRequest(new { error = $"Incoming folder does not exist: {incomingPath}" });

        var locations = await _db.LibraryLocations
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);

        var unknownRoots = await UnknownFolderResolver.GetSourceRootsAsync(_db, locations, ct);

        var warnings = new List<string>();
        foreach (var unknownRoot in unknownRoots)
        {
            if (!Directory.Exists(unknownRoot)) continue;
            foreach (var dir in Directory.GetDirectories(unknownRoot))
            {
                var folderName = Path.GetFileName(dir);
                var dest = UniqueDirectory(incomingPath, folderName);
                try
                {
                    Directory.CreateDirectory(dest);
                    foreach (var entry in Directory.GetFileSystemEntries(dir))
                    {
                        var name = Path.GetFileName(entry);
                        var target = Path.Combine(dest, name);
                        if (Directory.Exists(entry)) Directory.Move(entry, target);
                        else System.IO.File.Move(entry, target, overwrite: false);
                    }
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch (IOException ex) { warnings.Add($"{folderName}: {ex.Message}"); }
            }
        }

        if (warnings.Count > 0)
            return Ok(new { warnings });

        return NoContent();
    }

    // Kicks off the author-folder disambiguator (also runs daily via Hangfire
    // at 11:00). Returns the previous run's summary if one is already in
    // flight; otherwise schedules a new run and returns 202 Accepted.
    [HttpPost("disambiguate-folders")]
    public ActionResult<object> DisambiguateFolders(
        [FromServices] AuthorFolderDisambiguatorService service,
        CancellationToken ct)
    {
        if (service.IsRunning)
            return Accepted(new { running = true, lastResult = service.LastResult });
        if (!service.TryStart(ct, out var error))
            return Conflict(new { error });
        return Accepted(new { running = true });
    }

    [HttpGet("disambiguate-folders/status")]
    public ActionResult<object> DisambiguateFoldersStatus(
        [FromServices] AuthorFolderDisambiguatorService service)
    {
        return Ok(new { running = service.IsRunning, lastResult = service.LastResult });
    }

    public sealed record OlSuggestion(string OpenLibraryKey, string Name, int? WorkCount, double Score);

    // Given a folder name, returns top OpenLibrary author candidates ranked by
    // a fuzzy score against the folder name. Used on the Untracked page so a
    // user can promote a quarantined folder to a tracked author in one click
    // without typing into the search dialog. Rate-limited via the shared OL
    // limiter so a barrage of folder names won't violate OL's 1/sec ceiling.
    [HttpGet("~/api/openlibrary/suggest-for-folder")]
    public async Task<ActionResult<IReadOnlyList<OlSuggestion>>> SuggestForFolder(
        [FromQuery] string folder, CancellationToken ct,
        [FromQuery] int top = 3)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return BadRequest(new { error = "folder is required" });

        // Calibre's "Last, First" gets reordered for the OL query so the
        // search picks up the canonical "First Last" form OL prefers.
        var query = folder;
        if (query.Contains(','))
        {
            var parts = query.Split(',', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2) query = $"{parts[1]} {parts[0]}";
        }

        var resp = await _ol.SearchAuthorsAsync(query, ct);
        var folderKey = TitleNormalizer.NormalizeAuthor(folder);
        var suggestions = resp?.Docs
            ?.Where(d => !string.IsNullOrEmpty(d.Key) && !string.IsNullOrEmpty(d.Name))
            .Select(d => new OlSuggestion(
                d.Key!, d.Name!, d.WorkCount,
                FuzzyScore.JaroWinkler(folderKey, TitleNormalizer.NormalizeAuthor(d.Name!))))
            .OrderByDescending(s => s.Score)
            .Take(Math.Clamp(top, 1, 10))
            .ToList()
            ?? new List<OlSuggestion>();

        return Ok(suggestions);
    }

    public sealed record UnknownMatchResult(
        string FolderName,
        int AuthorId,
        string AuthorName);

    public sealed record UnknownMatchSummary(
        int Matched,
        int Unmatched,
        IReadOnlyList<UnknownMatchResult> Details,
        IReadOnlyList<string> Warnings);

    // Tries to re-match every __unknown folder against the current watchlist
    // (including OL alternate names). Matched folders are moved out of __unknown
    // and into the canonical author's folder; unmatched folders stay put. Run
    // this after adding authors so previously-quarantined collections fold back
    // in without a full sync.
    [HttpPost("~/api/unknown-folders/match")]
    public async Task<ActionResult<UnknownMatchSummary>> MatchUnknownFolders(CancellationToken ct)
    {
        // Build the matcher with tracked authors plus OL alternates joined by
        // OpenLibraryKey. Non-pen-name linked children are skipped so a folder
        // resolves directly to its canonical entry.
        var authors = await _db.Authors
            .Where(a => a.LinkedToAuthorId == null || a.IsPenName)
            .Select(a => new { a.Id, a.Name, a.CalibreFolderName, a.OpenLibraryKey })
            .ToListAsync(ct);

        var olKeys = authors
            .Where(a => !string.IsNullOrEmpty(a.OpenLibraryKey))
            .Select(a => a.OpenLibraryKey!)
            .Distinct()
            .ToList();
        var olAlternates = await _db.OpenLibraryAuthors
            .Where(o => olKeys.Contains(o.OlKey))
            .Select(o => new { o.OlKey, o.AlternateNames, o.PersonalName })
            .ToDictionaryAsync(o => o.OlKey, ct);

        var blacklisted = (await _db.AuthorBlacklist
            .Select(b => b.NormalizedName)
            .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var entries = authors.Select(a =>
        {
            var aliases = new List<string>();
            if (!string.IsNullOrEmpty(a.OpenLibraryKey)
                && olAlternates.TryGetValue(a.OpenLibraryKey, out var ol))
            {
                if (!string.IsNullOrWhiteSpace(ol.PersonalName)) aliases.Add(ol.PersonalName);
                if (!string.IsNullOrWhiteSpace(ol.AlternateNames))
                    aliases.AddRange(ol.AlternateNames.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            return new AuthorIndexEntry(
                DisplayName: a.Name,
                FolderName: string.IsNullOrWhiteSpace(a.CalibreFolderName) ? a.Name : a.CalibreFolderName!,
                IsTracked: true,
                TrackedAuthorId: a.Id,
                OpenLibraryKey: a.OpenLibraryKey,
                AlternateNames: aliases.Count == 0 ? null : aliases);
        });
        var matcher = new AuthorMatcher(entries, blacklisted);

        var locations = await _db.LibraryLocations
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);

        var customUnknown = await UnknownFolderResolver.GetCustomPathAsync(_db, ct);
        var primaryLocation = await _db.LibraryLocations
            .Where(l => l.Enabled && l.IsPrimary)
            .Select(l => l.Path)
            .FirstOrDefaultAsync(ct)
            ?? locations.FirstOrDefault();
        // Per-location pairs of (where to scan, where to move matches into).
        var scanPlan = customUnknown is not null
            ? new[] { (UnknownRoot: customUnknown, DestRoot: primaryLocation ?? customUnknown) }
            : locations.Select(l => (UnknownRoot: Path.Combine(l, CalibreScanner.UnknownAuthorFolder), DestRoot: l)).ToArray();

        var warnings = new List<string>();
        var details = new List<UnknownMatchResult>();
        int unmatched = 0;

        foreach (var (unknownRoot, root) in scanPlan)
        {
            if (!Directory.Exists(unknownRoot)) continue;

            var folderNames = Directory.EnumerateDirectories(unknownRoot)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Cast<string>()
                .ToList();

            var plan = UnknownFolderRecovery.Plan(folderNames, matcher);
            unmatched += plan.Unmatched.Count;

            foreach (var decision in plan.Matched)
            {
                var src = Path.Combine(unknownRoot, decision.FolderName);
                if (!Directory.Exists(src)) continue;
                var entry = decision.Match!;
                var destLeaf = string.IsNullOrWhiteSpace(entry.FolderName) ? entry.DisplayName : entry.FolderName;
                var dest = Path.Combine(root, SanitizeSegment(destLeaf));

                try
                {
                    if (Directory.Exists(dest))
                    {
                        // Canonical folder already exists — merge by moving each
                        // child entry across rather than failing the rename.
                        foreach (var child in Directory.GetFileSystemEntries(src))
                        {
                            var childName = Path.GetFileName(child);
                            var target = UniqueDirectory(dest, childName);
                            if (Directory.Exists(child)) Directory.Move(child, target);
                            else System.IO.File.Move(child, target, overwrite: false);
                        }
                        if (!Directory.EnumerateFileSystemEntries(src).Any())
                            Directory.Delete(src);
                    }
                    else
                    {
                        Directory.Move(src, dest);
                    }

                    if (entry.TrackedAuthorId is int authorId)
                        details.Add(new UnknownMatchResult(decision.FolderName, authorId, entry.DisplayName));
                }
                catch (IOException ex)
                {
                    warnings.Add($"{decision.FolderName}: {ex.Message}");
                }
            }
        }

        return Ok(new UnknownMatchSummary(details.Count, unmatched, details, warnings));
    }
}

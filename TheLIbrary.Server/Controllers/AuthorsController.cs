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
public partial class AuthorsController : ControllerBase
{
    private readonly LibraryDbContext _db;
    private readonly OpenLibraryClient _ol;
    private readonly AuthorRefresher _refresher;
    private readonly ManualBookService _manualBooks;
    private readonly IFileSystem _fs;
    private readonly ILogger<AuthorsController> _log;
    private readonly CalibreConverter _converter;
    private readonly Services.Sync.ContentScanService _contentScan;
    private readonly UntrackedAuthorAssigner _assigner;
    private readonly IHostApplicationLifetime _lifetime;

    public AuthorsController(
        LibraryDbContext db,
        OpenLibraryClient ol,
        AuthorRefresher refresher,
        ManualBookService manualBooks,
        IFileSystem fs,
        ILogger<AuthorsController> log,
        CalibreConverter converter,
        Services.Sync.ContentScanService contentScan,
        UntrackedAuthorAssigner assigner,
        IHostApplicationLifetime lifetime)
    {
        _db = db;
        _ol = ol;
        _refresher = refresher;
        _manualBooks = manualBooks;
        _fs = fs;
        _log = log;
        _converter = converter;
        _contentScan = contentScan;
        _assigner = assigner;
        _lifetime = lifetime;
    }

    // Reads the front matter of this author's unmatched files to guess their
    // book/series, capped by ContentScanMaxPerRun. Results show on the
    // Identified Books page. POST /api/authors/{id}/content-scan
    [HttpPost("{id:int}/content-scan")]
    public async Task<IActionResult> ContentScan(int id, CancellationToken ct)
    {
        var exists = await _db.Authors.AnyAsync(a => a.Id == id, ct);
        if (!exists) return NotFound(new { error = "Author not found." });
        // Runs in the background on the host lifetime token — reading files
        // (especially Calibre-converted ones) can take minutes, far longer than
        // the browser will hold the request open. Progress shows on the Sync page.
        if (!_contentScan.TryStartAuthor(id, _lifetime.ApplicationStopping, out var err))
            return Conflict(new { error = err });
        return Accepted(new { started = true });
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
        DateTime? LastSyncedAt,
        string? CreationSource = null);

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
                a.LastSyncedAt,
                a.CreationSource
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
                       SUM(CASE WHEN lf.BookId IS NULL AND (b.ManuallyOwned = 1 OR b.OwnedDifferentEdition = 1) THEN 1 ELSE 0 END) AS Physical
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
                ebook, physical, r.LastSyncedAt, r.CreationSource);
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

        // Count only rows the author page would actually show as unmatched: an
        // ebook file (by extension), with a non-blank title, not under the archive
        // folder. Counting every BookId==null row (folder-shaped, blank-title, or
        // archived rows included) is why the star count read 15 while the detail
        // page showed none. The extension test isn't SQL-translatable, so pull the
        // (curated, starred-only) candidate paths and filter in memory.
        var archiveLeafRaw = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.DedupeArchiveFolder).Select(s => s.Value).FirstOrDefaultAsync(ct);
        var archiveLeaf = string.IsNullOrWhiteSpace(archiveLeafRaw) ? "__archive" : archiveLeafRaw.Trim();

        var unmatchedFiles = await _db.LocalBookFiles.AsNoTracking()
            .Where(f => f.BookId == null && f.AuthorId != null && ids.Contains(f.AuthorId.Value))
            .Select(f => new { f.AuthorId, f.TitleFolder, f.FullPath })
            .ToListAsync(ct);
        var unmatchedDict = unmatchedFiles
            .Where(f => !string.IsNullOrWhiteSpace(f.TitleFolder)
                        && HasEbookExtension(f.FullPath)
                        && !IsUnderArchiveFolder(f.FullPath, archiveLeaf))
            .GroupBy(f => f.AuthorId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        return authors.Select(a => new StarredAuthorRow(
            a.Id, a.Name, a.Priority,
            bookCounts.GetValueOrDefault(a.Id),
            ebookCounts.GetValueOrDefault(a.Id),
            unmatchedDict.GetValueOrDefault(a.Id)
        )).ToList();
    }

    public sealed record StalledAuthorRow(
        int Id,
        string Name,
        string? OpenLibraryKey,
        string Status,
        int UnmatchedFileCount,
        int? CanonicalId,
        string? CanonicalName);

    // Returns authors who have unmatched ebook files with no possibility of being
    // auto-matched: their book catalogue has no unsuppressed, non-foreign entries.
    // Pending authors are excluded because they haven't been catalogued yet.
    //
    // Linked non-pen-name children fold their files into the canonical; the
    // canonical row is what appears (or is suppressed if the canonical itself has
    // matchable books). Pen-name children are independent and can appear on their
    // own if they qualify.
    [HttpGet("stalled")]
    public async Task<IReadOnlyList<StalledAuthorRow>> GetStalled(CancellationToken ct)
    {
        var archiveLeafRaw = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.DedupeArchiveFolder)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        var archiveLeaf = string.IsNullOrWhiteSpace(archiveLeafRaw) ? "__archive" : archiveLeafRaw.Trim();

        // Fetch all authors with their linkage fields — we need the full set to
        // resolve canonical-author relationships.
        var allAuthors = await _db.Authors.AsNoTracking()
            .Select(a => new
            {
                a.Id,
                a.Name,
                a.OpenLibraryKey,
                a.Status,
                a.LinkedToAuthorId,
                a.IsPenName
            })
            .ToListAsync(ct);

        // Authors whose Status == Pending are excluded from the page entirely.
        var nonPending = allAuthors
            .Where(a => a.Status != AuthorStatus.Pending)
            .ToList();

        // Per-author unmatched ebook file count, computed ENTIRELY in SQL.
        // This page used to materialize every unmatched LocalBookFile row
        // (two path strings per row, across the whole library) just to count
        // them in memory — that transfer is what made the page take so long
        // to load. The grouped count is a few hundred small rows instead;
        // authors that turn out to be irrelevant are simply never looked up.
        var leaf = archiveLeaf.Replace('\\', '/').TrimEnd('/');
        var archivePrefix = leaf + "/";
        var archiveSegment = "/" + leaf + "/";
        var fileQuery = _db.LocalBookFiles.AsNoTracking()
            .Where(f => f.BookId == null && f.AuthorId != null
                && f.TitleFolder != null && f.TitleFolder != ""
                && (f.FullPath.EndsWith(".epub") || f.FullPath.EndsWith(".mobi")
                 || f.FullPath.EndsWith(".azw") || f.FullPath.EndsWith(".azw3")
                 || f.FullPath.EndsWith(".azw4") || f.FullPath.EndsWith(".kf8")
                 || f.FullPath.EndsWith(".prc") || f.FullPath.EndsWith(".pdb")
                 || f.FullPath.EndsWith(".fb2") || f.FullPath.EndsWith(".fbz")
                 || f.FullPath.EndsWith(".pdf") || f.FullPath.EndsWith(".lit")
                 || f.FullPath.EndsWith(".cbz") || f.FullPath.EndsWith(".docx")
                 || f.FullPath.EndsWith(".odt") || f.FullPath.EndsWith(".rtf")
                 || f.FullPath.EndsWith(".opf") || f.FullPath.EndsWith(".txt")));
        fileQuery = leaf.Contains('/')
            ? fileQuery.Where(f => !f.FullPath.Replace("\\", "/").StartsWith(archivePrefix))
            : fileQuery.Where(f => !f.FullPath.Replace("\\", "/").Contains(archiveSegment));
        var rawUnmatchedByAuthor = (await fileQuery
            .GroupBy(f => f.AuthorId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct))
            .ToDictionary(x => x.Key, x => x.Count);

        // Authors (by id) that have at least one matchable book — id list only.
        var authorsWithMatchableBooks = (await _db.Books.AsNoTracking()
            .Where(b => !b.Suppressed && !b.Foreign)
            .Select(b => b.AuthorId)
            .Distinct()
            .ToListAsync(ct))
            .ToHashSet();

        var results = new List<StalledAuthorRow>();

        foreach (var author in nonPending)
        {
            // Non-pen-name linked children are always folded into their canonical;
            // they don't get a separate row on this page.
            if (author.LinkedToAuthorId.HasValue && !author.IsPenName)
                continue;

            // Build the set of author ids whose files and books count for this row:
            // the author itself plus any non-pen-name linked children.
            var childIds = allAuthors
                .Where(a => a.LinkedToAuthorId == author.Id && !a.IsPenName)
                .Select(a => a.Id)
                .ToList();

            var familyIds = new HashSet<int> { author.Id };
            foreach (var cid in childIds) familyIds.Add(cid);

            // If any member of the family has matchable books the whole group is fine.
            if (familyIds.Any(id => authorsWithMatchableBooks.Contains(id)))
                continue;

            // Sum unmatched files across the whole family.
            var fileCount = familyIds.Sum(id => rawUnmatchedByAuthor.GetValueOrDefault(id));
            if (fileCount == 0)
                continue;

            // For a pen-name child that has its own row, surface which canonical
            // it belongs to.
            int? canonicalId = author.IsPenName && author.LinkedToAuthorId.HasValue
                ? author.LinkedToAuthorId
                : null;
            string? canonicalName = canonicalId.HasValue
                ? allAuthors.FirstOrDefault(a => a.Id == canonicalId.Value)?.Name
                : null;

            results.Add(new StalledAuthorRow(
                author.Id,
                author.Name,
                author.OpenLibraryKey,
                author.Status.ToString(),
                fileCount,
                canonicalId,
                canonicalName));
        }

        return results.OrderBy(r => r.Name).ToList();
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
        IReadOnlyList<LinkedAuthorRef> PenNames,
        DateTime? CalibreScannedAt = null,
        // Unmatched files belonging to OTHER, distinct authors that share this
        // author's name (homonyms split across separate Author rows / OL keys).
        // Shown below this author's own unmatched list so the user can adopt a
        // mis-filed copy onto one of THIS author's works. Null/empty when none.
        IReadOnlyList<SameNameUnmatchedGroup>? SameNameUnmatched = null,
        // Other, not-yet-linked authors whose name is SIMILAR to this one
        // (spelling/initials variants and exact homonyms) — candidates to link
        // under this author as canonical. Highest similarity first.
        IReadOnlyList<SimilarAuthor>? SimilarAuthors = null,
        // True when the user dismissed this author from the Recommendations page.
        // The author detail page surfaces an "undo" so a rejection is recoverable.
        bool RecommendationRejected = false);

    // A link suggestion: another author whose name resembles this one.
    public sealed record SimilarAuthor(int Id, string Name, string? OpenLibraryKey, string Status, double Score);

    // One same-name author's unmatched files, for the "other authors with this
    // name" section. Grouped so the UI can collapse/expand per author.
    public sealed record SameNameUnmatchedGroup(
        int AuthorId,
        string AuthorName,
        string? OpenLibraryKey,
        string Status,
        IReadOnlyList<UnmatchedRow> Files);

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
        bool Suppressed = false,
        bool Foreign = false,
        bool OwnedDifferentEdition = false,
        // Set only for books pulled in from a CO-AUTHOR of a shared series (so the
        // full series shows on every participating author's page). null = this
        // page's own book. The UI renders these read-only and excludes them from
        // the page's own counts/actions.
        string? OtherAuthorName = null);

    public sealed record LocalFileRow(int Id, string FullPath, IReadOnlyList<string> Formats, bool Archived);

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
        ".fb2", ".fbz", ".pdf", ".lit", ".cbz", ".docx", ".odt", ".rtf", ".opf", ".txt"
    };

    // True when the (forward-slash) path sits under the dedupe archive folder,
    // whether that's a simple leaf name or a full absolute path. Mirrors the
    // Duplicates / Damaged archive matching.
    private static bool IsUnderArchiveFolder(string fullPath, string archiveLeaf)
    {
        if (string.IsNullOrEmpty(fullPath)) return false;
        var leaf = archiveLeaf.Replace('\\', '/').TrimEnd('/');
        var p = fullPath.Replace('\\', '/');
        return leaf.Contains('/')
            ? p.StartsWith(leaf + "/", StringComparison.OrdinalIgnoreCase)
            : p.Contains("/" + leaf + "/", StringComparison.OrdinalIgnoreCase);
    }

    // FullPath may be a file path (flat-file layout after organizer runs) or a
    // directory path (classic library layout). Handle both so the UI shows formats.
    private static bool HasEbookExtension(string path)
        => !string.IsNullOrEmpty(path) && EbookExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    // The single source of truth for "does this LocalBookFile row represent a
    // real ebook?" — used everywhere a list must NOT show a folder-shaped or
    // empty row as a book / as ownership. A LocalBookFile.FullPath is either a
    // file (flat-file layout) or a title directory (classic library layout):
    //   - a file with an ebook extension          → real, format = its extension
    //   - a directory that holds >= 1 ebook        → real, formats = those inside
    //   - missing on disk but ebook-shaped path    → kept (a NAS mount glitch must
    //                                                 never hide a genuine file)
    //   - an empty / stale title-folder pointer,
    //     or a folder with no ebook                → NOT real (dropped)
    private static (IReadOnlyList<string> Formats, bool IsReal) ResolveLocalCopy(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return (Array.Empty<string>(), false);
        if (System.IO.File.Exists(path))
        {
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            return EbookExtensions.Contains("." + ext) ? (new[] { ext }, true) : (Array.Empty<string>(), false);
        }
        if (Directory.Exists(path))
        {
            var fmts = FormatsInFolder(path);
            return (fmts, fmts.Count > 0);
        }
        var e = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return e.Length > 0 && EbookExtensions.Contains("." + e)
            ? (new[] { e }, true)
            : (Array.Empty<string>(), false);
    }

    private static IReadOnlyList<string> FormatsInFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Array.Empty<string>();
        // FullPath may point at a single file (flat-file layout: one
        // LocalBookFile row per file) or at a title directory (classic library
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
                    b.AuthorId, b.ManuallyOwned, b.OwnedDifferentEdition, b.ReadStatus, b.ReadAt, b.Wanted, b.Subjects, b.Suppressed, b.Foreign,
                    SeriesName = b.Series != null ? b.Series.Name : null,
                    b.SeriesId,
                    SeriesPrimaryAuthorName = b.Series != null && b.Series.PrimaryAuthor != null ? b.Series.PrimaryAuthor.Name : null,
                    b.SeriesPosition,
                    Files = b.LocalFiles.Select(f => new { f.Id, f.FullPath }).ToList()
                })
                .ToListAsync(ct);

        // Files living under the dedupe archive folder are flagged so the UI can
        // tuck them behind an "archived" expander instead of cluttering the book.
        var archiveLeafRaw = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.DedupeArchiveFolder)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);
        var archiveLeaf = string.IsNullOrWhiteSpace(archiveLeafRaw) ? "__archive" : archiveLeafRaw.Trim();

        var books = rawBooks.Select(b =>
        {
            // Resolve each linked row against disk and DROP the phantoms: a row
            // whose path is an empty/stale title folder (or a folder holding no
            // ebook) is not a book copy and must not make this book look owned
            // or appear as a file. This is the read-path guarantee that a folder
            // is never shown as a book, independent of when the prune job last ran.
            var realFiles = b.Files
                .Select(f => { var (fmts, ok) = ResolveLocalCopy(f.FullPath); return (f.Id, f.FullPath, Formats: fmts, IsReal: ok); })
                .Where(f => f.IsReal)
                .ToList();
            return new BookRow(
                b.Id, b.Title, b.NormalizedTitle, b.FirstPublishYear, b.CoverId, b.OpenLibraryWorkKey,
                b.ManuallyOwned || b.OwnedDifferentEdition || realFiles.Count > 0,
                b.ManuallyOwned,
                realFiles.Count > 0,
                b.ReadStatus.ToString(),
                b.ReadAt,
                b.Wanted,
                b.Subjects,
                b.SeriesName,
                b.SeriesPosition,
                realFiles.Select(f => new LocalFileRow(f.Id, f.FullPath, f.Formats, IsUnderArchiveFolder(f.FullPath, archiveLeaf))).ToList(),
                b.SeriesId,
                b.SeriesPrimaryAuthorName,
                b.CoverUrl,
                b.AuthorId,
                b.Suppressed,
                b.Foreign,
                b.OwnedDifferentEdition
            );
        }).ToList();

        // Shared/co-authored series: when one of THIS author's series also holds
        // volumes by OTHER authors, pull those volumes in so the FULL series shows
        // on every participating author's page — not just each author's own slice.
        // They're tagged with OtherAuthorName and carry no file detail (DB-level
        // owned flag only, no NAS reads); the UI renders them read-only and keeps
        // the page's own counts/actions to this author's books.
        var ownSeriesIds = books
            .Where(b => b.SeriesId != null)
            .Select(b => b.SeriesId!.Value)
            .Distinct()
            .ToList();
        if (ownSeriesIds.Count > 0)
        {
            var coAuthorBooks = await _db.Books.AsNoTracking()
                .Where(b => b.SeriesId != null && ownSeriesIds.Contains(b.SeriesId.Value)
                         && !foldedIds.Contains(b.AuthorId)
                         && !b.Suppressed)
                .OrderBy(b => b.Series!.Name).ThenBy(b => b.SeriesPosition)
                    .ThenBy(b => b.FirstPublishYear ?? int.MaxValue).ThenBy(b => b.Title)
                .Select(b => new
                {
                    b.Id, b.Title, b.NormalizedTitle, b.FirstPublishYear, b.CoverId, b.CoverUrl, b.OpenLibraryWorkKey,
                    b.AuthorId, AuthorName = b.Author.Name, b.ManuallyOwned, b.OwnedDifferentEdition,
                    b.ReadStatus, b.ReadAt, b.Wanted, b.Subjects, b.Foreign,
                    SeriesName = b.Series != null ? b.Series.Name : null, b.SeriesId,
                    SeriesPrimaryAuthorName = b.Series != null && b.Series.PrimaryAuthor != null ? b.Series.PrimaryAuthor.Name : null,
                    b.SeriesPosition,
                    HasFiles = b.LocalFiles.Any(),
                })
                .ToListAsync(ct);

            books.AddRange(coAuthorBooks.Select(b => new BookRow(
                b.Id, b.Title, b.NormalizedTitle, b.FirstPublishYear, b.CoverId, b.OpenLibraryWorkKey,
                b.ManuallyOwned || b.OwnedDifferentEdition || b.HasFiles,
                b.ManuallyOwned,
                b.HasFiles,
                b.ReadStatus.ToString(),
                b.ReadAt,
                b.Wanted,
                b.Subjects,
                b.SeriesName,
                b.SeriesPosition,
                new List<LocalFileRow>(),
                b.SeriesId,
                b.SeriesPrimaryAuthorName,
                b.CoverUrl,
                b.AuthorId,
                false,
                b.Foreign,
                b.OwnedDifferentEdition,
                b.AuthorName)));
        }

        // Include orphan rows (AuthorId == null) whose library folder matches
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
            // Only show rows that are an actual ebook: a file with an ebook
            // extension, or a folder that directly contains one. This drops
            // folder-shaped / empty rows (e.g. ".../David Gemmell" that only holds
            // book subfolders, or the bare author-folder row whose title is blank)
            // and anything already archived.
            .Where(r => !string.IsNullOrWhiteSpace(r.TitleFolder)
                        && (r.Formats.Count > 0 || HasEbookExtension(r.FullPath))
                        && !IsUnderArchiveFolder(r.FullPath, archiveLeaf))
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

        var sameNameGroups = await BuildSameNameUnmatchedAsync(a, foldedIds, archiveLeaf, ct);
        var similarAuthors = await BuildSimilarAuthorsAsync(a, ct);

        return new AuthorDetail(
            a.Id, a.Name, a.OpenLibraryKey, a.CalibreFolderName,
            a.Status.ToString(), a.ExclusionReason, a.Priority, a.LastSyncedAt, a.NextFetchAt, a.RefreshIntervalDays,
            a.Bio, a.Notes, a.NotifyOnNewBooks,
            books, unmatched, associatedSeries,
            linkedTo, alternates, penNames, a.CalibreScannedAt, sameNameGroups, similarAuthors,
            a.RecommendationRejected);
    }

    // Confidence floor for offering an author as a "similar name" link suggestion.
    private const double SimilarAuthorFloor = 0.84;

    // Suggests other, not-yet-linked authors whose name resembles this one so
    // the user can link them under this author (spelling/initials variants like
    // "Iain Banks" / "Iain M. Banks", and exact homonyms). A SQL prefilter on a
    // shared first/last name token keeps this off the full ~120k-row table; the
    // survivors are Jaro-Winkler scored in memory. Only LINKABLE candidates are
    // returned: not this author, not already linked to anyone, and not itself a
    // canonical with children (the link endpoint refuses those).
    private async Task<IReadOnlyList<SimilarAuthor>> BuildSimilarAuthorsAsync(Author current, CancellationToken ct)
    {
        // Only a top-level author can be a canonical target. A child / pen-name
        // page doesn't offer link suggestions (it isn't the primary).
        if (current.LinkedToAuthorId is not null) return Array.Empty<SimilarAuthor>();

        var targetNorm = TitleNormalizer.NormalizeAuthor(current.Name);
        if (string.IsNullOrWhiteSpace(targetNorm)) return Array.Empty<SimilarAuthor>();
        var tokens = (current.Name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return Array.Empty<SimilarAuthor>();
        var firstTok = tokens[0];
        var lastTok = tokens[^1];

        // Prefilter: same exact name, OR shares the surname (ends with " last"),
        // OR shares the first name (starts with "first "). Capped so a very
        // common surname can't pull the whole table into memory.
        var candidates = await _db.Authors.AsNoTracking()
            .Where(a => a.Id != current.Id
                     && a.LinkedToAuthorId == null
                     && a.Status != AuthorStatus.Excluded
                     && (a.Name == current.Name
                         || a.Name.EndsWith(" " + lastTok)
                         || a.Name.StartsWith(firstTok + " ")))
            .OrderBy(a => a.Id)
            .Select(a => new { a.Id, a.Name, a.OpenLibraryKey, a.Status })
            .Take(2000)
            .ToListAsync(ct);
        if (candidates.Count == 0) return Array.Empty<SimilarAuthor>();

        // Drop candidates that are themselves a canonical for other rows — the
        // link endpoint rejects linking those without unlinking first.
        var candidateIds = candidates.Select(c => c.Id).ToList();
        var haveChildren = (await _db.Authors.AsNoTracking()
                .Where(a => a.LinkedToAuthorId != null && candidateIds.Contains(a.LinkedToAuthorId!.Value))
                .Select(a => a.LinkedToAuthorId!.Value)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();

        return candidates
            .Where(c => !haveChildren.Contains(c.Id))
            .Select(c => new { c, Score = FuzzyScore.JaroWinkler(targetNorm, TitleNormalizer.NormalizeAuthor(c.Name)) })
            .Where(x => x.Score >= SimilarAuthorFloor)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.c.Name, StringComparer.OrdinalIgnoreCase)
            .Take(15)
            .Select(x => new SimilarAuthor(x.c.Id, x.c.Name, x.c.OpenLibraryKey, x.c.Status.ToString(), Math.Round(x.Score, 3)))
            .ToList();
    }

    // Finds OTHER, distinct authors sharing this author's (normalized) name and
    // returns each one's unmatched ebook files, grouped by author. The current
    // author's own folded family (itself + non-pen-name children) is excluded —
    // their files are already in the page's own unmatched list. Only real ebook
    // rows are returned (same disk-reality filter as the main unmatched list),
    // so empty/stale folder pointers never appear here either.
    private async Task<IReadOnlyList<SameNameUnmatchedGroup>> BuildSameNameUnmatchedAsync(
        Author current, IReadOnlyList<int> foldedIds, string archiveLeaf, CancellationToken ct)
    {
        var targetNorm = TitleNormalizer.NormalizeAuthor(current.Name);
        if (string.IsNullOrWhiteSpace(targetNorm)) return Array.Empty<SameNameUnmatchedGroup>();

        // Prefilter in SQL on exact (case-insensitive) name equality — that's how
        // homonyms actually appear in the data (the same-name-authors job copies
        // the name verbatim across OL keys) — then confirm by normalized name in
        // memory to absorb punctuation/spacing variants among that small set.
        var folded = foldedIds.ToHashSet();
        var candidates = (await _db.Authors.AsNoTracking()
                .Where(x => x.Name == current.Name && x.Status != AuthorStatus.Excluded)
                .Select(x => new { x.Id, x.Name, x.OpenLibraryKey, x.Status })
                .ToListAsync(ct))
            .Where(x => !folded.Contains(x.Id)
                     && TitleNormalizer.NormalizeAuthor(x.Name) == targetNorm)
            .ToList();
        if (candidates.Count == 0) return Array.Empty<SameNameUnmatchedGroup>();

        var candidateIds = candidates.Select(c => c.Id).ToHashSet();
        var rawFiles = await _db.LocalBookFiles.AsNoTracking()
            .Where(f => f.BookId == null && f.AuthorId != null && candidateIds.Contains(f.AuthorId!.Value))
            .OrderBy(f => f.TitleFolder)
            .Select(f => new { f.Id, f.AuthorId, f.TitleFolder, f.FullPath })
            .ToListAsync(ct);

        var byAuthor = rawFiles
            .Select(f => new { f.AuthorId, Row = new UnmatchedRow(f.Id, f.TitleFolder, f.FullPath, FormatsInFolder(f.FullPath)) })
            .Where(x => !string.IsNullOrWhiteSpace(x.Row.TitleFolder)
                     && (x.Row.Formats.Count > 0 || HasEbookExtension(x.Row.FullPath))
                     && !IsUnderArchiveFolder(x.Row.FullPath, archiveLeaf))
            .GroupBy(x => x.AuthorId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Row).ToList());

        return candidates
            .Where(c => byAuthor.ContainsKey(c.Id))
            .OrderBy(c => c.Name)
            .Select(c => new SameNameUnmatchedGroup(
                c.Id, c.Name, c.OpenLibraryKey, c.Status.ToString(), byAuthor[c.Id]))
            .ToList();
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
    // an orphan row whose library folder matches the author. Endpoints that
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

    // A guessed title is only suggested when it really resembles a book — high
    // enough that Jaro-Winkler "noise" matches (a long unrelated title sharing
    // common letters scores ~0.55-0.65) never surface. Genuine matches, even with
    // trailing "(retail) (epub)" cruft, score ~0.9.
    private const double SuggestionMinScore = 0.75;

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

        // A book titled as the author themself is an OpenLibrary artifact, not a
        // real work — never offer it as a match candidate.
        var authorNameNorm = TitleNormalizer.Normalize(author.Name);
        var books = (await _db.Books.AsNoTracking()
            // Don't offer a book the user has flagged foreign or hidden — they've
            // already decided they don't want it, so it shouldn't be a target.
            .Where(b => foldedIds.Contains(b.AuthorId) && !b.Suppressed && !b.Foreign)
            .Select(b => new
            {
                b.Id, b.Title, b.NormalizedTitle,
                SeriesName = b.Series != null ? b.Series.Name : null,
                b.SeriesPosition,
            })
            .ToListAsync(ct))
            .Where(b => string.IsNullOrEmpty(authorNameNorm)
                || (b.NormalizedTitle ?? TitleNormalizer.Normalize(b.Title)) != authorNameNorm)
            .ToList();

        var folderCandidates = FolderCandidatesFor(author);
        var unmatched = await _db.LocalBookFiles.AsNoTracking()
            .Where(f => f.BookId == null
                && (foldedIds.Contains(f.AuthorId ?? -1)
                    || (f.AuthorId == null && folderCandidates.Contains(f.AuthorFolder))))
            .Select(f => new { f.Id, f.TitleFolder, f.NormalizedTitle })
            .ToListAsync(ct);

        var authorNorm = TitleNormalizer.Normalize(author.Name);
        var result = new List<FileSuggestionSet>(unmatched.Count);
        foreach (var file in unmatched)
        {
            var stem = file.TitleFolder ?? "";
            var stems = SyncService.TitleStemCandidates(stem, author).ToList();
            var inferredRaw = stems.FirstOrDefault() ?? stem;

            // Candidate titles to score against — author-FREE only. Scoring a stem
            // that still contains the author name is what let any "<Author> - X"
            // file match a junk book whose title also starts with the author
            // (Jaro-Winkler's prefix bonus). We also score the BEST of all the
            // clean candidates (stripped form, series-parsed title, …) so messy
            // "Series N - Title" filenames still find their real book.
            var candidates = stems
                .SelectMany(TitleNormalizer.FolderTitleCandidates)
                .Where(c => !string.IsNullOrEmpty(c)
                            && c != authorNorm
                            && !(authorNorm.Length > 0 && c.StartsWith(authorNorm + " ", StringComparison.Ordinal)))
                .Distinct()
                .ToList();
            if (candidates.Count == 0) candidates.Add(TitleNormalizer.Normalize(inferredRaw));

            var scored = books
                .Select(b =>
                {
                    var bn = b.NormalizedTitle ?? TitleNormalizer.Normalize(b.Title);
                    double best = 0;
                    foreach (var c in candidates) best = Math.Max(best, FuzzyScore.JaroWinkler(bn, c));
                    return new BookSuggestion(b.Id, b.Title, best, b.SeriesName, b.SeriesPosition);
                })
                .OrderByDescending(s => s.Score)
                .Take(top)
                .Where(s => s.Score >= SuggestionMinScore)
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

    // Adopts a file that currently belongs to a DIFFERENT author who shares this
    // author's name (a homonym split across separate Author rows) onto one of
    // THIS author's works. The file is physically moved into this author's
    // folder — the author↔file link is folder-driven, so a DB-only reassignment
    // would revert on the next sync. POST /api/authors/{id}/same-name/{fileId}/match
    [HttpPost("{id:int}/same-name/{fileId:int}/match")]
    public async Task<ActionResult<AuthorDetail>> AdoptSameNameFile(
        int id, int fileId, [FromBody] MatchLocalFileRequest body, CancellationToken ct)
    {
        var author = await _db.Authors.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (author is null) return NotFound(new { error = "Author not found" });

        var file = await _db.LocalBookFiles.FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null) return NotFound(new { error = "Local file not found" });
        if (file.AuthorId is null || file.AuthorId == id)
            return BadRequest(new { error = "File does not belong to a different author." });

        // Guard: only adopt files from an author who genuinely shares this name.
        // Prevents this endpoint being used as an arbitrary cross-author move.
        var sourceAuthor = await _db.Authors.FirstOrDefaultAsync(x => x.Id == file.AuthorId, ct);
        if (sourceAuthor is null
            || TitleNormalizer.NormalizeAuthor(sourceAuthor.Name) != TitleNormalizer.NormalizeAuthor(author.Name))
            return BadRequest(new { error = "File's author does not share this author's name." });

        var book = await _db.Books.FirstOrDefaultAsync(b => b.Id == body.BookId, ct);
        if (book is null) return NotFound(new { error = "Book not found" });
        if (!await BookBelongsToAuthorViewAsync(book, id, ct))
            return BadRequest(new { error = "Book does not belong to this author" });

        // Folder-driven move into this author's folder (sets AuthorId/AuthorFolder/
        // FullPath, resets integrity, prunes the now-empty source folder), then link.
        await MoveFileToAuthorFolderAsync(file, author, ct);
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

        // Use the existing TitleFolder name when we have one (normal library
        // layout), otherwise the AuthorFolder (scanner recorded an empty
        // TitleFolder when the file was directly under the author dir).
        var leafName = !string.IsNullOrWhiteSpace(file.TitleFolder)
            ? file.TitleFolder
            : file.AuthorFolder;
        if (string.IsNullOrWhiteSpace(leafName)) leafName = $"returned-{file.Id}";

        var destPath = UniqueDirectory(incomingPath, leafName);

        try
        {
            SafeMove.Directory(source, destPath);
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

    // Archives an unmatched file: moves it under the dedupe archive folder
    // (preserving its library-relative path) and repoints its DB row(s) there, so
    // it drops out of the unmatched list but can still be restored. Use for a
    // foreign / unwanted file you don't want to link to a book.
    [HttpPost("{id:int}/unmatched/{fileId:int}/archive")]
    public async Task<ActionResult<AuthorDetail>> ArchiveUnmatched(int id, int fileId, CancellationToken ct)
    {
        var author = await _db.Authors.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (author is null) return NotFound(new { error = "Author not found" });
        var file = await _db.LocalBookFiles.FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null) return NotFound(new { error = "Local file not found" });
        if (!FileBelongsToAuthor(file, author)) return BadRequest(new { error = "File does not belong to this author" });

        var root = await LibraryRootForAsync(file.FullPath, ct);
        if (root is null) return BadRequest(new { error = "File is outside all enabled library locations." });
        var libRoot = root.TrimEnd('\\', '/');
        var leaf = await LoadArchiveLeafAsync(ct);
        var relative = file.FullPath.Length > libRoot.Length
            ? file.FullPath[libRoot.Length..].TrimStart('\\', '/')
            : Path.GetFileName(file.FullPath);
        var destBase = leaf.Contains('/') || leaf.Contains('\\') ? leaf.Replace('\\', '/') : Path.Combine(libRoot, leaf);
        var destPath = Path.Combine(destBase, relative);

        try
        {
            string final;
            if (System.IO.File.Exists(file.FullPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                final = UniqueFilePath(destPath);
                SafeMove.File(file.FullPath, final);
            }
            else if (Directory.Exists(file.FullPath))
            {
                var parent = Path.GetDirectoryName(destPath) ?? destBase;
                Directory.CreateDirectory(parent);
                final = UniqueDirectoryPath(parent, Path.GetFileName(destPath));
                SafeMove.Directory(file.FullPath, final);
            }
            else return BadRequest(new { error = "File no longer exists on disk." });

            await RepointRowsAsync(file.FullPath, final, ct);
            await PruneEmptyParentsAsync(Path.GetDirectoryName(file.FullPath), libRoot, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StatusCode(500, new { error = $"Archive failed: {ex.Message}" });
        }

        await _db.SaveChangesAsync(ct);
        return await Get(id, ct);
    }

    // Permanently deletes an unmatched file from disk and removes its DB row(s).
    [HttpDelete("{id:int}/unmatched/{fileId:int}")]
    public async Task<ActionResult<AuthorDetail>> DeleteUnmatched(int id, int fileId, CancellationToken ct)
    {
        var author = await _db.Authors.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (author is null) return NotFound(new { error = "Author not found" });
        var file = await _db.LocalBookFiles.FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null) return NotFound(new { error = "Local file not found" });
        if (!FileBelongsToAuthor(file, author)) return BadRequest(new { error = "File does not belong to this author" });

        var root = await LibraryRootForAsync(file.FullPath, ct);
        try
        {
            if (System.IO.File.Exists(file.FullPath)) System.IO.File.Delete(file.FullPath);
            else if (Directory.Exists(file.FullPath)) Directory.Delete(file.FullPath, recursive: true);
            if (root is not null)
                await PruneEmptyParentsAsync(Path.GetDirectoryName(file.FullPath), root.TrimEnd('\\', '/'), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StatusCode(500, new { error = $"Delete failed: {ex.Message}" });
        }

        await RemoveRowsUnderAsync(file.FullPath, ct);
        await _db.SaveChangesAsync(ct);
        return await Get(id, ct);
    }

    // Moves an unmatched file into the __unknown quarantine bucket and drops its
    // DB row(s), so it's re-evaluated as untracked (with no author) on the next
    // sync. Use when the file is filed under the WRONG author.
    [HttpPost("{id:int}/unmatched/{fileId:int}/to-unknown")]
    public async Task<ActionResult<AuthorDetail>> SendUnmatchedToUnknown(int id, int fileId, CancellationToken ct)
    {
        var author = await _db.Authors.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (author is null) return NotFound(new { error = "Author not found" });
        var file = await _db.LocalBookFiles.FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null) return NotFound(new { error = "Local file not found" });
        if (!FileBelongsToAuthor(file, author)) return BadRequest(new { error = "File does not belong to this author" });

        var root = await LibraryRootForAsync(file.FullPath, ct);
        if (root is null) return BadRequest(new { error = "File is outside all enabled library locations." });
        var libRoot = root.TrimEnd('\\', '/');
        var custom = await UnknownFolderResolver.GetCustomPathAsync(_db, ct);
        var unknownBase = custom ?? Path.Combine(libRoot, CalibreScanner.UnknownAuthorFolder);
        Directory.CreateDirectory(unknownBase);

        var leafName = !string.IsNullOrWhiteSpace(file.TitleFolder)
            ? file.TitleFolder
            : Path.GetFileName(file.FullPath.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(leafName)) leafName = $"unknown-{file.Id}";

        try
        {
            if (System.IO.File.Exists(file.FullPath))
            {
                // Flat: a single file drops straight into the quarantine root.
                var final = UniqueFilePath(Path.Combine(unknownBase, Path.GetFileName(file.FullPath)));
                SafeMove.File(file.FullPath, final);
            }
            else if (Directory.Exists(file.FullPath))
            {
                // FLATTEN a folder-shaped row's files into the root — never
                // create an author/title subfolder under __unknown.
                UnknownQuarantine.FlattenFolderIntoRoot(unknownBase, file.FullPath);
            }
            else return BadRequest(new { error = "File no longer exists on disk." });

            await PruneEmptyParentsAsync(Path.GetDirectoryName(file.FullPath), libRoot, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StatusCode(500, new { error = $"Move failed: {ex.Message}" });
        }

        await RemoveRowsUnderAsync(file.FullPath, ct);
        await _db.SaveChangesAsync(ct);
        return await Get(id, ct);
    }

    private async Task<string> LoadArchiveLeafAsync(CancellationToken ct)
    {
        var raw = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.DedupeArchiveFolder).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(raw) ? "__archive" : raw.Trim();
    }

    private async Task<string?> LibraryRootForAsync(string fullPath, CancellationToken ct)
    {
        var roots = await _db.LibraryLocations.AsNoTracking().Where(l => l.Enabled).Select(l => l.Path).ToListAsync(ct);
        return roots.FirstOrDefault(r =>
            fullPath.StartsWith(r.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
    }

    // After moving oldPath → newPath on disk, repoint every DB row at or under the
    // old path to the new path (handles a moved folder's child file rows too).
    private async Task RepointRowsAsync(string oldPath, string newPath, CancellationToken ct)
    {
        var old = oldPath.TrimEnd('\\', '/');
        var prefix = old + Path.DirectorySeparatorChar;
        var rows = await _db.LocalBookFiles
            .Where(f => f.FullPath == old || f.FullPath.StartsWith(prefix))
            .ToListAsync(ct);
        foreach (var r in rows)
            r.FullPath = string.Equals(r.FullPath, old, StringComparison.Ordinal)
                ? newPath
                : newPath + r.FullPath[old.Length..];
    }

    private async Task RemoveRowsUnderAsync(string path, CancellationToken ct)
    {
        var p = path.TrimEnd('\\', '/');
        var prefix = p + Path.DirectorySeparatorChar;
        var rows = await _db.LocalBookFiles
            .Where(f => f.FullPath == p || f.FullPath.StartsWith(prefix))
            .ToListAsync(ct);
        _db.LocalBookFiles.RemoveRange(rows);
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

    private static string SanitizeSegment(string name)
        => UntrackedAuthorAssigner.SanitizeSegment(name);

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
            // Already tracked — still run adoption so any unmatched library files
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
            Status = AuthorStatus.Pending,
            CreationSource = "manual"
        };

        // If a library folder name matches, link it now so the user can see that
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

        var (book, error) = await ApplyOpenLibraryWorkToFileAsync(currentAuthor, file, body, ct);
        if (error is not null) return BadRequest(new { error });
        await _db.SaveChangesAsync(ct);
        return await Get(id, ct);
    }

    // Core of "link this file to an OpenLibrary work": resolve the target author,
    // create/reuse the book, move the file into the author's folder if it changed,
    // and set the link. Shared by the manual OL-match endpoint and the
    // apply-content-guess action. Does NOT SaveChanges — the caller does.
    private async Task<(Book? Book, string? Error)> ApplyOpenLibraryWorkToFileAsync(
        Author currentAuthor, LocalBookFile file, MatchOpenLibraryFileRequest body, CancellationToken ct,
        bool keepCurrentAuthor = false)
    {
        // When the file is already sitting in an author folder, that folder *is*
        // the author (the link is folder-driven) — so don't re-resolve the author
        // from the OpenLibrary work and risk moving the file elsewhere. Just bind
        // the matched work under the author it already belongs to.
        var targetAuthor = keepCurrentAuthor
            ? currentAuthor
            : await ResolveTargetAuthorAsync(currentAuthor, body.PrimaryAuthorKey, body.PrimaryAuthorName, body.Authors, ct);
        if (targetAuthor is null) return (null, "Could not determine the OpenLibrary author for this work");

        var add = await EnsureOpenLibraryBookAsync(
            targetAuthor.Id, body.WorkKey, body.Title, body.FirstPublishYear, body.CoverId, owned: false, ct);
        if (add.Error is not null) return (null, add.Error);

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
        return (add.Book, null);
    }

    public sealed record ApplyGuessResult(bool Applied, int? BookId, int? AuthorId, string? Title, string? Reason);

    // Resolves a content-scan guess (ISBN, else title+author) to an OpenLibrary
    // work and links the file to it. Only works for tracked unmatched files
    // (untracked files have no author folder to attach to).
    // POST /api/authors/apply-content-guess/{scanId}
    [HttpPost("~/api/authors/apply-content-guess/{scanId:int}")]
    public async Task<ActionResult<ApplyGuessResult>> ApplyContentGuess(int scanId, CancellationToken ct)
    {
        var scan = await _db.BookContentScans.FirstOrDefaultAsync(c => c.Id == scanId, ct);
        if (scan is null) return NotFound(new { error = "Scan not found." });

        var file = await _db.LocalBookFiles.FirstOrDefaultAsync(f => f.FullPath == scan.FullPath, ct);
        if (file is null)
            return Ok(new ApplyGuessResult(false, null, null, null, "Only tracked unmatched files can be applied (this is an untracked file)."));
        if (file.BookId is not null)
            return Ok(new ApplyGuessResult(false, null, null, null, "File is already matched to a book."));
        if (file.AuthorId is null)
            return Ok(new ApplyGuessResult(false, null, null, null, "File isn't linked to an author."));

        var currentAuthor = await _db.Authors.FirstOrDefaultAsync(a => a.Id == file.AuthorId, ct);
        if (currentAuthor is null)
            return Ok(new ApplyGuessResult(false, null, null, null, "File's author no longer exists."));

        // Resolve the guess to an OpenLibrary work. ISBN is definitive. A title,
        // by contrast, is only a guess pulled from the book's text — so for the
        // title path we DON'T just take OL's first hit (that's how unrelated
        // works got attached). We know the real author (the folder), so we only
        // accept a work that is (a) actually by that author and (b) a close title
        // match; anything else is refused rather than guessed.
        WorkSearchDoc? doc = null;
        if (!string.IsNullOrWhiteSpace(scan.Isbn))
        {
            var byIsbn = await _ol.SearchByIsbnAsync(scan.Isbn, ct);
            doc = byIsbn?.Docs?.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.Key));
        }

        // No ISBN hit → match the guessed title against the author's OWN known
        // books (the DB list of valid titles) rather than inventing an OpenLibrary
        // work. We already know the author, and AuthorRefresher keeps their full OL
        // catalogue in the DB, so the right book is almost always already there.
        // Inventing a work from an OL title search is what attached wrong books.
        if (doc is null && !string.IsNullOrWhiteSpace(scan.Title))
        {
            var known = await FindBestKnownBookAsync(currentAuthor, scan.Title!, scan.SeriesPosition, ct);
            if (known is not null)
            {
                file.AuthorId = currentAuthor.Id;
                file.BookId = known.Id;
                file.ManuallyUnmatched = false;
                scan.Reviewed = true;
                await _db.SaveChangesAsync(ct);
                return Ok(new ApplyGuessResult(true, known.Id, file.AuthorId, known.Title, null));
            }

            // Nothing in the author's catalogue matched — do NOT guess an OpenLibrary
            // work. Leave the file for manual matching rather than attach something wrong.
            scan.Reviewed = true;
            await _db.SaveChangesAsync(ct);
            return Ok(new ApplyGuessResult(false, null, null, null,
                $"“{scan.Title}” doesn't closely match any of {currentAuthor.Name}'s known titles — not applied. Match it by hand if it's a new book."));
        }

        if (doc is null || string.IsNullOrWhiteSpace(doc.Key))
        {
            scan.Reviewed = true; // nothing to apply; clear it from the list
            await _db.SaveChangesAsync(ct);
            return Ok(new ApplyGuessResult(false, null, null, null, "No OpenLibrary work found for this guess."));
        }

        var req = new MatchOpenLibraryFileRequest(
            doc.Key, doc.Title, doc.FirstPublishYear, doc.CoverId,
            doc.AuthorNames is { Count: > 0 } ? string.Join(", ", doc.AuthorNames) : null,
            doc.AuthorKeys?.FirstOrDefault(),
            doc.AuthorNames?.FirstOrDefault());

        var (book, error) = await ApplyOpenLibraryWorkToFileAsync(currentAuthor, file, req, ct, keepCurrentAuthor: true);
        if (error is not null) return BadRequest(new { error });

        scan.Reviewed = true;
        await _db.SaveChangesAsync(ct);
        return Ok(new ApplyGuessResult(true, book!.Id, file.AuthorId, book.Title, null));
    }

    public sealed record CleanupNameBooksResult(int Removed);

    // One-shot maintenance: sweep the WHOLE library and delete phantom "books"
    // whose title is just their author's own name (the OpenLibrary artifact).
    // Same safety rules as the per-refresh cleanup — never a book with a local
    // file linked, never one the user marked owned by hand. POST
    // /api/authors/cleanup-name-books
    [HttpPost("~/api/authors/cleanup-name-books")]
    public async Task<ActionResult<CleanupNameBooksResult>> CleanupNameBooks(CancellationToken ct)
    {
        // author id -> normalized name (Normalize isn't SQL-translatable, so the
        // match is done in memory; the authors table is small enough to hold).
        var normByAuthor = new Dictionary<int, string>();
        foreach (var a in await _db.Authors.AsNoTracking().Select(a => new { a.Id, a.Name }).ToListAsync(ct))
        {
            var n = TitleNormalizer.Normalize(a.Name);
            if (!string.IsNullOrEmpty(n)) normByAuthor[a.Id] = n;
        }

        int removed = 0, afterId = 0;
        const int page = 2000;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await _db.Books.AsNoTracking()
                .Where(b => b.Id > afterId && !b.ManuallyOwned)
                .OrderBy(b => b.Id).Take(page)
                .Select(b => new { b.Id, b.AuthorId, b.Title, b.NormalizedTitle })
                .ToListAsync(ct);
            if (batch.Count == 0) break;
            afterId = batch[^1].Id;

            // A phantom is a book whose (effective) normalized title equals its
            // author's normalized name. Files mis-matched to it are unmatched, then
            // the book is deleted — not kept just because a file is attached.
            var candidateIds = batch
                .Where(b => normByAuthor.TryGetValue(b.AuthorId, out var n)
                         && (b.NormalizedTitle ?? TitleNormalizer.Normalize(b.Title)) == n)
                .Select(b => b.Id).ToList();
            if (candidateIds.Count == 0) continue;

            removed += await AuthorRefresher.DeletePhantomBooksAsync(_db, candidateIds, ct);
        }

        return Ok(new CleanupNameBooksResult(removed));
    }

    public sealed record AssignAuthorResult(bool Assigned, int? AuthorId, string? AuthorName, int? BookId, string? Path, string? Reason);

    // Aim 2: take an UNTRACKED __unknown file, work out whose it is, and file it
    // under that author (see UntrackedAuthorAssigner for the resolution order).
    // POST /api/identified/{scanId}/assign-author
    [HttpPost("~/api/identified/{scanId:int}/assign-author")]
    public async Task<ActionResult<AssignAuthorResult>> AssignUntrackedAuthor(int scanId, CancellationToken ct)
    {
        var scan = await _db.BookContentScans.FirstOrDefaultAsync(c => c.Id == scanId, ct);
        if (scan is null) return NotFound(new { error = "Scan not found." });
        try
        {
            var r = await _assigner.AssignAsync(scan, ct);
            return Ok(new AssignAuthorResult(r.Assigned, r.AuthorId, r.AuthorName, r.BookId, r.Path, r.Reason));
        }
        catch (Services.OpenLibrary.OpenLibraryRequestFailedException ex)
        {
            // OL refusing/failing the lookup is an upstream problem, not a
            // server fault — report it as the assign outcome, not a 500.
            return Ok(new AssignAuthorResult(false, null, null, null, null,
                $"OpenLibrary lookup failed: {ex.Message}"));
        }
    }

    public sealed record AssignAuthorsAllResult(int Assigned, int Skipped, int Failed, int Remaining, int? LastId);

    // Bulk version of assign-author: files every untracked row that has something to
    // resolve an author from (ISBN / title / guessed author) under its OL-resolved
    // or guessed author, creating authors as needed. Capped per call because
    // OpenLibrary is rate-limited; the afterId cursor lets the client sweep the
    // WHOLE backlog in one user action — pass the returned LastId back in until
    // Remaining is 0, and every row is attempted exactly once (skipped rows are
    // left behind the cursor instead of being retried forever at the front of the
    // queue). Rows that carry a series catalogue are kept (with their new author)
    // so their series can then be built with the same Build-series action as the
    // tracked rows. Also runs on a schedule (the "assign-authors" job).
    // POST /api/identified/assign-authors-all?afterId=N
    [HttpPost("~/api/identified/assign-authors-all")]
    public async Task<ActionResult<AssignAuthorsAllResult>> AssignUntrackedAuthorsAll(
        [FromQuery] int? afterId, CancellationToken ct)
    {
        const int cap = 100;
        var cursor = afterId ?? 0;
        var candidates = await _db.BookContentScans
            .Where(c => !c.Reviewed && c.AuthorId == null && c.Id > cursor
                && (c.Isbn != null || c.Author != null || c.Title != null))
            .OrderBy(c => c.Id)
            .Take(cap)
            .ToListAsync(ct);

        int assigned = 0, skipped = 0, failed = 0;
        foreach (var scan in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var r = await _assigner.AssignAsync(scan, ct);
                if (r.Assigned) assigned++; else skipped++;
            }
            catch { failed++; _db.ChangeTracker.Clear(); }
        }

        var lastId = candidates.Count > 0 ? candidates[^1].Id : (int?)null;
        var remaining = lastId is int last
            ? await _db.BookContentScans.CountAsync(c => !c.Reviewed && c.AuthorId == null && c.Id > last
                && (c.Isbn != null || c.Author != null || c.Title != null), ct)
            : 0;
        return Ok(new AssignAuthorsAllResult(assigned, skipped, failed, remaining, lastId));
    }

    // Minimum fuzzy score for auto-linking a content guess to one of the author's
    // EXISTING books. High on purpose: a scraped title must really be one of their
    // known titles, not just land near one. (Both sides are normalized first, so
    // articles/punctuation don't count.)
    private const double KnownTitleMinScore = 0.82;

    // A trailing "Book 3" / "Vol. 2" / "Part 1" / "#4" descriptor on a guessed
    // title ("High Druid of Shannara - Book 1") — stripped to form an extra match
    // candidate so the bare title can hit the real book.
    private static readonly System.Text.RegularExpressions.Regex TrailingVolumeRx = new(
        @"[\s:–—-]+(?:book|vol(?:ume)?|part|#)\s*\.?\s*\d+(?:\.\d+)?\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    // Finds the author's own existing book that best matches a content-guess title,
    // reusing the same author-prefix strip + series-filename parsing + Jaro-Winkler
    // fuzzy as the unmatched-file suggestions. Returns null when nothing clears the
    // floor — the caller then refuses rather than inventing an OpenLibrary work.
    // When the guess carries a series position, a same-position book is nudged up so
    // "X - Book 1" picks position 1 among several identically-prefixed titles.
    private async Task<Book?> FindBestKnownBookAsync(Author author, string guessedTitle, string? seriesPosition, CancellationToken ct)
    {
        var foldedIds = new List<int> { author.Id };
        foldedIds.AddRange(await _db.Authors.AsNoTracking()
            .Where(a => a.LinkedToAuthorId == author.Id && !a.IsPenName)
            .Select(a => a.Id).ToListAsync(ct));

        var authorNorm = TitleNormalizer.Normalize(author.Name);
        var books = (await _db.Books.AsNoTracking()
            .Where(b => foldedIds.Contains(b.AuthorId) && !b.Suppressed && !b.Foreign)
            .Select(b => new { b.Id, b.Title, b.NormalizedTitle, b.SeriesPosition })
            .ToListAsync(ct))
            // Never match a phantom book titled as the author themself.
            .Where(b => string.IsNullOrEmpty(authorNorm)
                || (b.NormalizedTitle ?? TitleNormalizer.Normalize(b.Title)) != authorNorm)
            .ToList();
        if (books.Count == 0) return null;

        var stems = SyncService.TitleStemCandidates(guessedTitle, author).ToList();
        var bare = TrailingVolumeRx.Replace(guessedTitle, "").Trim();
        if (bare.Length > 0 && !stems.Contains(bare)) stems.Add(bare);

        var candidates = stems
            .SelectMany(TitleNormalizer.FolderTitleCandidates)
            .Where(c => !string.IsNullOrEmpty(c)
                        && c != authorNorm
                        && !(authorNorm.Length > 0 && c.StartsWith(authorNorm + " ", StringComparison.Ordinal)))
            .Distinct()
            .ToList();
        if (candidates.Count == 0) return null;

        var hasPos = !string.IsNullOrWhiteSpace(seriesPosition);
        int? bestId = null;
        double bestScore = 0;
        foreach (var b in books)
        {
            var bn = b.NormalizedTitle ?? TitleNormalizer.Normalize(b.Title);
            double score = 0;
            foreach (var c in candidates) score = Math.Max(score, FuzzyScore.JaroWinkler(bn, c));
            if (hasPos && b.SeriesPosition == seriesPosition) score += 0.03; // position tiebreak
            if (score > bestScore) { bestScore = score; bestId = b.Id; }
        }

        return bestId is int id && bestScore >= KnownTitleMinScore
            ? await _db.Books.FirstOrDefaultAsync(b => b.Id == id, ct)
            : null;
    }

    public sealed record BulkApplyResult(int Applied, int Failed, int Remaining);

    // Bulk-applies every ISBN-backed guess (the high-confidence ones) for tracked
    // unmatched files, capped per call so the run can't time out — repeat until
    // Remaining is 0. POST /api/identified/apply-isbn-all
    [HttpPost("~/api/identified/apply-isbn-all")]
    public async Task<ActionResult<BulkApplyResult>> ApplyAllIsbnGuesses(CancellationToken ct)
    {
        const int cap = 100; // OpenLibrary is rate-limited; keep one call bounded.
        var scans = await _db.BookContentScans
            .Where(c => !c.Reviewed && c.Isbn != null
                && _db.LocalBookFiles.Any(f => f.FullPath == c.FullPath && f.BookId == null && f.AuthorId != null))
            .OrderBy(c => c.Id)
            .Take(cap)
            .ToListAsync(ct);

        int applied = 0, failed = 0;
        foreach (var scan in scans)
        {
            ct.ThrowIfCancellationRequested();
            var file = await _db.LocalBookFiles.FirstOrDefaultAsync(f => f.FullPath == scan.FullPath, ct);
            var currentAuthor = file?.AuthorId is int aid ? await _db.Authors.FirstOrDefaultAsync(a => a.Id == aid, ct) : null;
            if (file is null || file.BookId is not null || currentAuthor is null) continue;

            var doc = (await _ol.SearchByIsbnAsync(scan.Isbn!, ct))?.Docs?.FirstOrDefault();
            if (doc is null || string.IsNullOrWhiteSpace(doc.Key)) { scan.Reviewed = true; failed++; await _db.SaveChangesAsync(ct); continue; }

            var req = new MatchOpenLibraryFileRequest(
                doc.Key, doc.Title, doc.FirstPublishYear, doc.CoverId,
                doc.AuthorNames is { Count: > 0 } ? string.Join(", ", doc.AuthorNames) : null,
                doc.AuthorKeys?.FirstOrDefault(), doc.AuthorNames?.FirstOrDefault());

            var (_, error) = await ApplyOpenLibraryWorkToFileAsync(currentAuthor, file, req, ct, keepCurrentAuthor: true);
            if (error is not null) { failed++; continue; } // leave unreviewed to retry later
            scan.Reviewed = true;
            applied++;
            await _db.SaveChangesAsync(ct);
        }
        await _db.SaveChangesAsync(ct);

        var remaining = await _db.BookContentScans.CountAsync(c => !c.Reviewed && c.Isbn != null
            && _db.LocalBookFiles.Any(f => f.FullPath == c.FullPath && f.BookId == null && f.AuthorId != null), ct);
        return Ok(new BulkApplyResult(applied, failed, remaining));
    }

    // The OL author/book resolution and untracked-path move now live in
    // UntrackedAuthorAssigner (shared with the assign-authors scheduled job);
    // these thin wrappers keep this controller's many call sites unchanged.
    private Task<Author?> ResolveTargetAuthorAsync(
        Author? currentAuthor, string? primaryAuthorKey, string? primaryAuthorName,
        string? fallbackAuthors, CancellationToken ct)
        => _assigner.ResolveTargetAuthorAsync(currentAuthor, primaryAuthorKey, primaryAuthorName, fallbackAuthors, ct);

    private Task<EnsuredOpenLibraryBook> EnsureOpenLibraryBookAsync(
        int authorId, string? rawWorkKey, string? rawTitle, int? firstPublishYear,
        int? coverId, bool owned, CancellationToken ct)
        => _assigner.EnsureOpenLibraryBookAsync(authorId, rawWorkKey, rawTitle, firstPublishYear, coverId, owned, ct);

    private Task<string> MoveUntrackedPathToAuthorFolderAsync(
        string sourcePath, string rootPath, string? relativePath, Author targetAuthor, CancellationToken ct)
        => _assigner.MoveUntrackedPathToAuthorFolderAsync(sourcePath, rootPath, relativePath, targetAuthor, ct);

    private Task<(string? Root, string? Error)> ResolveDestinationRootAsync(string sourcePath, CancellationToken ct)
        => _assigner.ResolveDestinationRootAsync(sourcePath, ct);

    private static Task PruneEmptyParentsAsync(string? startPath, string stopRoot, CancellationToken ct)
        => UntrackedAuthorAssigner.PruneEmptyParentsAsync(startPath, stopRoot, ct);

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
        => UntrackedAuthorAssigner.NormalizeRelativePath(path);

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

        // Already in the target author folder — don't move onto self (otherwise
        // UniqueFilePath would see the existing file and rename it to "_2").
        if (FsPath.SameLocation(oldPath, destPath))
        {
            file.FullPath = oldPath;
            return;
        }

        if (System.IO.File.Exists(oldPath))
        {
            var destDir = Path.GetDirectoryName(destPath);
            if (destDir is not null) Directory.CreateDirectory(destDir);
            var final = UniqueFilePath(destPath);
            SafeMove.File(oldPath, final);
            file.FullPath = final;
            file.ResetIntegrity(); // moved into another author's folder — re-check it there
        }
        else if (Directory.Exists(oldPath))
        {
            var destParent = Path.GetDirectoryName(destPath);
            if (destParent is not null) Directory.CreateDirectory(destParent);
            var final = UniqueDirectoryPath(destParent ?? libRoot, Path.GetFileName(destPath));
            SafeMove.Directory(oldPath, final);
            file.FullPath = final;
            file.ResetIntegrity();
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
        => UntrackedAuthorAssigner.UniqueFilePath(desired);

    private static string UniqueDirectoryPath(string parent, string leaf)
        => UntrackedAuthorAssigner.UniqueDirectoryPath(parent, leaf);

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

    public sealed record BulkStatusRequest(IReadOnlyList<int> AuthorIds, string Status, string? ExclusionReason);
    public sealed record BulkStatusResult(int Updated);

    // Applies a status change (Active / Excluded / Pending) to a batch of authors at once.
    [HttpPost("bulk-status")]
    public async Task<ActionResult<BulkStatusResult>> BulkStatus([FromBody] BulkStatusRequest body, CancellationToken ct)
    {
        if (!Enum.TryParse<AuthorStatus>(body.Status, ignoreCase: true, out var status))
            return BadRequest(new { error = $"Unknown status '{body.Status}'" });

        var ids = (body.AuthorIds ?? Array.Empty<int>()).Distinct().ToList();
        if (ids.Count == 0) return Ok(new BulkStatusResult(0));

        var authors = await _db.Authors.Where(a => ids.Contains(a.Id)).ToListAsync(ct);

        // An author whose ebook files we physically hold is never excluded:
        // excluding them makes sync quarantine their folder into __unknown. Such
        // authors are skipped (the rest of the batch still applies).
        var withFiles = status == AuthorStatus.Excluded
            ? (await _db.LocalBookFiles
                .Where(f => f.AuthorId != null && ids.Contains(f.AuthorId.Value))
                .Select(f => f.AuthorId!.Value)
                .Distinct()
                .ToListAsync(ct)).ToHashSet()
            : new HashSet<int>();

        var updated = 0;
        foreach (var a in authors)
        {
            if (status == AuthorStatus.Excluded && withFiles.Contains(a.Id)) continue;
            a.Status = status;
            if (status == AuthorStatus.Excluded)
                a.ExclusionReason = string.IsNullOrWhiteSpace(body.ExclusionReason) ? null : body.ExclusionReason.Trim();
            else
                a.ExclusionReason = null;
            updated++;
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new BulkStatusResult(updated));
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

    public sealed record MergeAuthorRequest(int IntoAuthorId);
    public sealed record MergeAuthorResult(int BooksReassigned, int FilesReassigned);

    // Merges the current author INTO another author: all their Books and
    // LocalBookFiles are reassigned to the target, then this author is deleted.
    // Books that would create a duplicate OL work key on the target are skipped
    // (the merge still completes for the rest).
    [HttpPost("{id:int}/merge")]
    public async Task<ActionResult<MergeAuthorResult>> Merge(int id, [FromBody] MergeAuthorRequest body, CancellationToken ct)
    {
        if (id == body.IntoAuthorId)
            return BadRequest(new { error = "Cannot merge an author into themselves." });

        var source = await _db.Authors.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (source is null) return NotFound(new { error = "Source author not found." });

        var target = await _db.Authors.FirstOrDefaultAsync(a => a.Id == body.IntoAuthorId, ct);
        if (target is null) return BadRequest(new { error = "Target author not found." });

        // Target's existing books keyed by OL key — used both to skip duplicates
        // AND to re-home a duplicate's files onto the kept copy.
        var targetBooksByKey = await _db.Books
            .Where(b => b.AuthorId == body.IntoAuthorId && b.OpenLibraryWorkKey != null && b.OpenLibraryWorkKey != "")
            .ToDictionaryAsync(b => b.OpenLibraryWorkKey!, b => b.Id, ct);

        // All of the source's files (matched + unmatched), indexed by their book
        // so we can move a duplicate book's files before dropping the duplicate.
        var sourceFiles = await _db.LocalBookFiles.Where(f => f.AuthorId == id).ToListAsync(ct);
        var sourceFilesByBook = sourceFiles
            .Where(f => f.BookId != null)
            .GroupBy(f => f.BookId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var sourceBooks = await _db.Books.Where(b => b.AuthorId == id).ToListAsync(ct);
        int booksReassigned = 0;
        foreach (var book in sourceBooks)
        {
            if (!string.IsNullOrEmpty(book.OpenLibraryWorkKey)
                && targetBooksByKey.TryGetValue(book.OpenLibraryWorkKey, out var keepId))
            {
                // Duplicate of a target book: move this book's files onto the kept
                // copy, then drop the duplicate so the (AuthorId, OL key) unique
                // index is never violated and the files aren't left unmatched.
                if (sourceFilesByBook.TryGetValue(book.Id, out var dupFiles))
                    foreach (var f in dupFiles) f.BookId = keepId;
                _db.Books.Remove(book);
                continue;
            }
            book.AuthorId = body.IntoAuthorId;
            if (!string.IsNullOrEmpty(book.OpenLibraryWorkKey))
                targetBooksByKey[book.OpenLibraryWorkKey] = book.Id; // catch source-internal dups too
            booksReassigned++;
        }

        foreach (var f in sourceFiles) f.AuthorId = body.IntoAuthorId;

        // Also claim the source's UNMATCHED orphan files — rows with no AuthorId
        // that the detail page associates with the source only by folder name
        // (FolderCandidatesFor). Without this they'd be stranded once the source
        // author is deleted and wouldn't appear under the target.
        var sourceFolders = FolderCandidatesFor(source);
        var orphanFiles = await _db.LocalBookFiles
            .Where(f => f.AuthorId == null && f.BookId == null && sourceFolders.Contains(f.AuthorFolder))
            .ToListAsync(ct);
        foreach (var f in orphanFiles) f.AuthorId = body.IntoAuthorId;

        await _db.SaveChangesAsync(ct);
        _db.Authors.Remove(source);
        await _db.SaveChangesAsync(ct);

        return Ok(new MergeAuthorResult(booksReassigned, sourceFiles.Count + orphanFiles.Count));
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

                if (FsPath.SameLocation(file.FullPath, destPath))
                {
                    // Already in the canonical folder — don't move onto self.
                }
                else if (System.IO.File.Exists(file.FullPath))
                {
                    var finalDest = UniqueFile(destPath);
                    SafeMove.File(file.FullPath, finalDest);
                    file.FullPath = finalDest;
                }
                else if (Directory.Exists(file.FullPath))
                {
                    var finalDest = UniqueDirectory(Path.GetDirectoryName(destPath)!, Path.GetFileName(destPath));
                    SafeMove.Directory(file.FullPath, finalDest);
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
                SafeMove.Directory(src, dest);
            }
            catch (IOException ex)
            {
                moveWarnings.Add($"{leaf}: {ex.Message}");
            }
        }

        // If the author's library author-level folders are now empty, prune
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

}

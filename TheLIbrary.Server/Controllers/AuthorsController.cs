using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
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
    private readonly BackgroundTaskCoordinator _coordinator;

    public AuthorsController(
        LibraryDbContext db,
        OpenLibraryClient ol,
        AuthorRefresher refresher,
        BackgroundTaskCoordinator coordinator)
    {
        _db = db;
        _ol = ol;
        _refresher = refresher;
        _coordinator = coordinator;
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
        var baseRows = await _db.Authors.AsNoTracking()
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

    public sealed record AuthorDetail(
        int Id,
        string Name,
        string? OpenLibraryKey,
        string? CalibreFolderName,
        string Status,
        string? ExclusionReason,
        int Priority,
        DateTime? LastSyncedAt,
        IReadOnlyList<BookRow> Books,
        IReadOnlyList<UnmatchedRow> UnmatchedLocal);

    public sealed record BookRow(
        int Id,
        string Title,
        int? FirstPublishYear,
        int? CoverId,
        string OpenLibraryWorkKey,
        bool Owned,
        bool ManuallyOwned,
        bool HasLocalFiles,
        IReadOnlyList<LocalFileRow> Files);

    public sealed record LocalFileRow(int Id, string FullPath, IReadOnlyList<string> Formats);

    public sealed record UnmatchedRow(int Id, string TitleFolder, string FullPath, IReadOnlyList<string> Formats);

    // Extensions we recognize as ebook files in the incoming pipeline. Any
    // other file in the title folder (cover.jpg, metadata.opf, …) is ignored
    // so the UI doesn't show "jpg" as a sendable format.
    private static readonly HashSet<string> EbookExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".mobi", ".azw", ".azw3", ".azw4", ".kf8", ".prc", ".pdb",
        ".fb2", ".fbz", ".pdf", ".lit", ".cbz", ".docx", ".odt"
    };

    // FullPath on LocalBookFile is the title folder, not an individual file.
    // Enumerate it once per row so the UI can show actual formats (and pick
    // which files are sendable to reMarkable).
    private static IReadOnlyList<string> FormatsInFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return Array.Empty<string>();
        try
        {
            return Directory.EnumerateFiles(folderPath)
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

        // Projected into an intermediate shape so we can enumerate the
        // per-file folder on disk for formats outside the EF query.
        var rawBooks = await _db.Books.AsNoTracking()
            .Where(b => b.AuthorId == id)
            .OrderBy(b => b.FirstPublishYear ?? int.MaxValue).ThenBy(b => b.Title)
            .Select(b => new
            {
                b.Id, b.Title, b.FirstPublishYear, b.CoverId, b.OpenLibraryWorkKey,
                b.ManuallyOwned,
                Files = b.LocalFiles.Select(f => new { f.Id, f.FullPath }).ToList()
            })
            .ToListAsync(ct);

        var books = rawBooks.Select(b => new BookRow(
            b.Id, b.Title, b.FirstPublishYear, b.CoverId, b.OpenLibraryWorkKey,
            b.ManuallyOwned || b.Files.Count > 0,
            b.ManuallyOwned,
            b.Files.Count > 0,
            b.Files.Select(f => new LocalFileRow(f.Id, f.FullPath, FormatsInFolder(f.FullPath))).ToList()
        )).ToList();

        // Include orphan rows (AuthorId == null) whose Calibre folder matches
        // this author by name or recorded folder. Happens when the user adds
        // the author to the watchlist after sync had already recorded files
        // into the orphan pool — otherwise the folder would stay invisible
        // until the next full sync relinked it.
        var folderCandidates = FolderCandidatesFor(a);
        var rawUnmatched = await _db.LocalBookFiles.AsNoTracking()
            .Where(f => f.BookId == null
                && (f.AuthorId == id
                    || (f.AuthorId == null && folderCandidates.Contains(f.AuthorFolder))))
            .OrderBy(f => f.TitleFolder)
            .Select(f => new { f.Id, f.TitleFolder, f.FullPath })
            .ToListAsync(ct);

        var unmatched = rawUnmatched
            .Select(f => new UnmatchedRow(f.Id, f.TitleFolder, f.FullPath, FormatsInFolder(f.FullPath)))
            .ToList();

        return new AuthorDetail(
            a.Id, a.Name, a.OpenLibraryKey, a.CalibreFolderName,
            a.Status.ToString(), a.ExclusionReason, a.Priority, a.LastSyncedAt,
            books, unmatched);
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
        if (book.AuthorId != id)
            return BadRequest(new { error = "Book does not belong to this author" });

        file.AuthorId = id;
        file.BookId = book.Id;
        await _db.SaveChangesAsync(ct);
        return await Get(id, ct);
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

    public sealed record AddAuthorRequest(string OpenLibraryKey, string? Name);

    // Adds an author to the watchlist from an OpenLibrary key.
    // If Name is omitted we resolve it via OL search.
    [HttpPost]
    public async Task<ActionResult<AuthorListItem>> Add([FromBody] AddAuthorRequest body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.OpenLibraryKey))
            return BadRequest(new { error = "OpenLibraryKey is required" });

        var key = body.OpenLibraryKey.Trim();
        // Accept "/authors/OL1234A" or "OL1234A".
        if (key.StartsWith("/authors/", StringComparison.OrdinalIgnoreCase))
            key = key[("/authors/".Length)..];

        var existing = await _db.Authors.FirstOrDefaultAsync(a => a.OpenLibraryKey == key, ct);
        if (existing is not null)
            return Conflict(new { error = "Author already in watchlist", id = existing.Id });

        var name = body.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            var search = await _ol.SearchAuthorsAsync(key, ct);
            name = search?.Docs.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase))?.Name
                   ?? key;
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

        return CreatedAtAction(nameof(Get), new { id = author.Id }, new AuthorListItem(
            author.Id, author.Name, author.CalibreFolderName, author.OpenLibraryKey,
            author.Status.ToString(), author.ExclusionReason,
            author.Priority, 0, 0, 0, 0, author.LastSyncedAt));
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

    // On-demand refresh of a single author: resolves the OL key if missing,
    // fetches English works, reapplies exclusion rules, and reschedules.
    // Uses the same global coordinator as the full sync so this can't run
    // concurrently with any other background task.
    [HttpPost("{id:int}/refresh")]
    public async Task<ActionResult<AuthorDetail>> Refresh(int id, CancellationToken ct)
    {
        var author = await _db.Authors.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (author is null) return NotFound();

        if (!_coordinator.TryAcquire($"refresh author {author.Name}", out var holder))
            return Conflict(new { error = $"Another task is already running ({holder})" });

        try
        {
            await _refresher.RefreshAsync(author, onMessage: null, ct);
        }
        finally
        {
            _coordinator.Release();
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

    public sealed record UnclaimedFolder(string AuthorFolder, int FileCount);

    // Calibre author folders that don't match any tracked author.
    [HttpGet("~/api/unclaimed")]
    public async Task<IReadOnlyList<UnclaimedFolder>> Unclaimed(CancellationToken ct)
    {
        var rows = await _db.LocalBookFiles.AsNoTracking()
            .Where(f => f.AuthorId == null)
            .GroupBy(f => f.AuthorFolder)
            .Select(g => new { Folder = g.Key, Count = g.Count() })
            .OrderBy(r => r.Folder)
            .ToListAsync(ct);
        return rows.Select(r => new UnclaimedFolder(r.Folder, r.Count)).ToList();
    }
}

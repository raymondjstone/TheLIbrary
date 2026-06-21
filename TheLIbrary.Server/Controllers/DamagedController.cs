using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.IO;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Controllers;

// Read + action surface for the Damaged page: lists ebook files the integrity
// job flagged as unopenable / unconvertible / too short, lets the user re-queue
// one for a fresh check, mark a false positive as OK, archive a genuinely-bad
// file out of the library, and kick off the job on demand.
[ApiController]
[Route("api/damaged")]
public class DamagedController : ControllerBase
{
    private readonly LibraryDbContext _db;
    private readonly BookIntegrityService _integrity;
    private readonly IFileSystem _fs;
    private readonly IHostApplicationLifetime _lifetime;

    public DamagedController(
        LibraryDbContext db, BookIntegrityService integrity, IFileSystem fs, IHostApplicationLifetime lifetime)
    {
        _db = db;
        _integrity = integrity;
        _fs = fs;
        _lifetime = lifetime;
    }

    public sealed record DamagedFile(
        int Id,
        string Path,
        string? Format,
        long SizeBytes,
        int? Pages,
        string? Error,
        DateTime? CheckedAt);

    // Damaged files grouped by their book (mirrors the Duplicates page), so all
    // bad copies of one book sit together and can be archived in one action.
    public sealed record DamagedGroup(
        int? BookId,
        string Title,
        int? AuthorId,
        string AuthorName,
        int AuthorPriority,   // 0–5; "starred" is >= 1, matching the rest of the app
        IReadOnlyList<DamagedFile> Files);

    public sealed record JobStatus(bool Running, string? Message, int DamagedCount, int BacklogCount = 0);
    public sealed record ArchiveResult(bool Archived, string? NewPath, string? Warning);

    // Another file linked to the same book as a damaged file — a candidate
    // replacement. Archived copies are included (and flagged) so a good copy
    // that was swept into the archive can be restored in place of the bad one.
    public sealed record AlternateFile(
        int Id, string Path, string? Format, long SizeBytes, bool Archived, bool? IntegrityOk);

    /// <summary>Damaged files grouped by book, by author then title. GET /api/damaged</summary>
    [HttpGet]
    public async Task<IReadOnlyList<DamagedGroup>> GetDamaged(CancellationToken ct = default)
    {
        var archiveLeaf = await GetArchiveLeafAsync(ct);

        var rows = await _db.LocalBookFiles.AsNoTracking()
            .Where(f => f.IntegrityOk == false)
            .Where(ArchivedFilesController.NotUnderArchive(archiveLeaf))
            .Select(f => new
            {
                f.Id,
                f.FullPath,
                f.BookId,
                f.AuthorFolder,
                f.TitleFolder,
                f.SizeBytes,
                f.IntegrityPages,
                f.IntegrityError,
                f.IntegrityCheckedAt,
                BookTitle = f.Book != null ? f.Book.Title : null,
                BookAuthorId = f.Book != null ? (int?)f.Book.AuthorId : null,
                BookAuthorName = f.Book != null ? f.Book.Author.Name : null,
                BookAuthorPriority = f.Book != null ? (int?)f.Book.Author.Priority : null,
            })
            .ToListAsync(ct);

        // Group by book when linked; fall back to author+title folder so loose
        // damaged files (whose book was deleted) still cluster sensibly. Only real
        // ebook FILES belong here — drop any directory / extensionless rows that an
        // older check flagged and the current job (ebook-extension only) never
        // re-evaluates, so they don't sit here forever as un-previewable ". files".
        return rows
            .Where(r => BookIntegrityChecker.IsEbook(r.FullPath))
            .GroupBy(r => r.BookId.HasValue ? $"b:{r.BookId.Value}" : $"f:{r.AuthorFolder}|{r.TitleFolder}")
            .Select(g =>
            {
                var first = g.First();
                var files = g
                    .OrderByDescending(r => r.IntegrityCheckedAt)
                    .Select(r => new DamagedFile(
                        r.Id, r.FullPath, FormatOf(r.FullPath), r.SizeBytes,
                        r.IntegrityPages, r.IntegrityError, r.IntegrityCheckedAt))
                    .ToList();
                var title = first.BookId.HasValue
                    ? (first.BookTitle ?? first.TitleFolder ?? "(untitled)")
                    : (first.TitleFolder ?? "(untitled)");
                var authorName = (first.BookId.HasValue ? first.BookAuthorName : null)
                    ?? first.AuthorFolder ?? "";
                var authorId = first.BookId.HasValue ? first.BookAuthorId : null;
                var authorPriority = (first.BookId.HasValue ? first.BookAuthorPriority : null) ?? 0;
                return new DamagedGroup(first.BookId, title, authorId, authorName, authorPriority, files);
            })
            .OrderBy(grp => grp.AuthorName).ThenBy(grp => grp.Title)
            .ToList();
    }

    /// <summary>Current job status + damaged count for the page header. GET /api/damaged/status</summary>
    [HttpGet("status")]
    public async Task<JobStatus> GetStatus(CancellationToken ct = default)
    {
        var archiveLeaf = await GetArchiveLeafAsync(ct);
        // Count only real ebook files, matching GetDamaged — the extension test
        // isn't SQL-translatable, so pull the (small) damaged path set and filter.
        var paths = await _db.LocalBookFiles.AsNoTracking()
            .Where(f => f.IntegrityOk == false)
            .Where(ArchivedFilesController.NotUnderArchive(archiveLeaf))
            .Select(f => f.FullPath)
            .ToListAsync(ct);
        var count = paths.Count(BookIntegrityChecker.IsEbook);

        // Backlog: files still awaiting a first/again integrity check — the same
        // candidate predicate the job uses (book/author-linked, stamp missing or
        // stale, not archived). A fast SQL COUNT; the job's in-memory ebook filter
        // trims a little off this, so treat it as an upper bound for the gauge.
        var backlog = await _db.LocalBookFiles.AsNoTracking()
            .Where(f => f.BookId != null || f.AuthorId != null)
            .Where(f => f.IntegrityCheckedSize == null
                     || f.IntegrityCheckedSize != f.SizeBytes
                     || f.IntegrityCheckedModified != f.ModifiedAt)
            .Where(ArchivedFilesController.NotUnderArchive(archiveLeaf))
            .CountAsync(ct);
        return new JobStatus(_integrity.IsRunning, _integrity.CurrentMessage, count, backlog);
    }

    /// <summary>
    /// Re-queues a single file: clears its stored check fingerprint so the next
    /// job run re-checks it. POST /api/damaged/{id}/recheck
    /// </summary>
    [HttpPost("{id:int}/recheck")]
    public async Task<IActionResult> Recheck(int id, CancellationToken ct)
    {
        var file = await _db.LocalBookFiles.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (file is null) return NotFound(new { error = "File not found." });

        file.IntegrityCheckedSize = null;
        file.IntegrityCheckedModified = null;
        file.IntegrityOk = null;
        file.IntegrityError = null;
        file.IntegrityPages = null;
        file.IntegrityCheckedAt = null;
        await _db.SaveChangesAsync(ct);
        return Ok(new { requeued = true });
    }

    /// <summary>
    /// Marks a flagged file as actually fine (a false positive). It leaves the
    /// Damaged list and — because the stored fingerprint is kept current — won't
    /// be re-flagged until the file changes on disk. POST /api/damaged/{id}/mark-ok
    /// </summary>
    [HttpPost("{id:int}/mark-ok")]
    public async Task<IActionResult> MarkOk(int id, CancellationToken ct)
    {
        var file = await _db.LocalBookFiles.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (file is null) return NotFound(new { error = "File not found." });

        file.IntegrityOk = true;
        file.IntegrityError = null;
        // Stamp both the size and modified time so the job leaves this file
        // alone until one of them actually changes on disk.
        file.IntegrityCheckedSize = file.SizeBytes;
        file.IntegrityCheckedModified = file.ModifiedAt;
        file.IntegrityCheckedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { ok = true });
    }

    /// <summary>
    /// Archives a damaged file: moves it into the dedupe archive folder
    /// (preserving its library-relative subpath, same as the Duplicates page) so
    /// it leaves the library and the Damaged list but stays recoverable from the
    /// Archived Files page. POST /api/damaged/{id}/archive
    /// </summary>
    [HttpPost("{id:int}/archive")]
    public async Task<ActionResult<ArchiveResult>> Archive(int id, CancellationToken ct)
    {
        var file = await _db.LocalBookFiles.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (file is null) return NotFound(new { error = "File not found." });
        if (string.IsNullOrWhiteSpace(file.FullPath))
            return BadRequest(new { error = "File has no path recorded." });

        var archiveLeaf = await GetArchiveLeafAsync(ct);
        var locations = await _db.LibraryLocations.AsNoTracking()
            .Where(l => l.Enabled).Select(l => l.Path).ToListAsync(ct);

        var (ok, warning) = await ArchiveFileAsync(file, archiveLeaf, locations, ct);
        if (!ok) return Ok(new ArchiveResult(false, null, warning));

        await _db.SaveChangesAsync(ct);
        // The row keeps IntegrityOk == false, but now lives under the archive
        // folder so it's excluded from the Damaged list and shows on Archived Files.
        return Ok(new ArchiveResult(true, file.FullPath, null));
    }

    public sealed record ArchiveReplacedResult(int Archived, int Skipped, IReadOnlyList<string> Warnings);
    public sealed record ArchiveFilesRequest(IReadOnlyList<int>? FileIds);
    public const string DefaultReplacementFormats = "epub;mobi;lit";

    /// <summary>
    /// Archives a set of files in one pass — used by the Damaged page's per-book
    /// "Archive all bad copies" action, which sends every damaged file of a book.
    /// POST /api/damaged/archive-files
    /// </summary>
    [HttpPost("archive-files")]
    public async Task<ActionResult<ArchiveReplacedResult>> ArchiveFiles(
        [FromBody] ArchiveFilesRequest body, CancellationToken ct)
    {
        if (body.FileIds is null || body.FileIds.Count == 0)
            return BadRequest(new { error = "At least one file id is required." });

        var archiveLeaf = await GetArchiveLeafAsync(ct);
        var locations = await _db.LibraryLocations.AsNoTracking()
            .Where(l => l.Enabled).Select(l => l.Path).ToListAsync(ct);
        var files = await _db.LocalBookFiles
            .Where(f => body.FileIds.Contains(f.Id))
            .ToListAsync(ct);

        var warnings = new List<string>();
        int archived = 0, skipped = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var (ok, warning) = await ArchiveFileAsync(f, archiveLeaf, locations, ct);
            if (ok) archived++;
            else { skipped++; if (warning is not null) warnings.Add($"#{f.Id}: {warning}"); }
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new ArchiveReplacedResult(archived, skipped, warnings));
    }

    /// <summary>
    /// Archives every damaged file that has a healthy same-book copy in one of
    /// the configured replacement formats (Settings → IntegrityReplacementFormats,
    /// default epub/mobi/lit). The damaged duplicate moves to the archive folder;
    /// the good copy stays put. POST /api/damaged/archive-replaced
    /// </summary>
    [HttpPost("archive-replaced")]
    public async Task<ActionResult<ArchiveReplacedResult>> ArchiveReplaced(CancellationToken ct)
    {
        var formats = await GetReplacementFormatsAsync(ct);
        var archiveLeaf = await GetArchiveLeafAsync(ct);
        var locations = await _db.LibraryLocations.AsNoTracking()
            .Where(l => l.Enabled).Select(l => l.Path).ToListAsync(ct);

        // Damaged files still in the library (not already archived), linked to a book.
        var damaged = await _db.LocalBookFiles
            .Where(f => f.IntegrityOk == false && f.BookId != null)
            .Where(ArchivedFilesController.NotUnderArchive(archiveLeaf))
            .ToListAsync(ct);
        if (damaged.Count == 0) return Ok(new ArchiveReplacedResult(0, 0, Array.Empty<string>()));

        var bookIds = damaged.Select(f => f.BookId!.Value).Distinct().ToList();
        var siblings = await _db.LocalBookFiles.AsNoTracking()
            .Where(f => f.BookId != null && bookIds.Contains(f.BookId.Value))
            .Select(f => new { f.Id, f.BookId, f.FullPath, f.IntegrityOk })
            .ToListAsync(ct);

        // Books that have at least one healthy replacement copy: a live
        // (non-archived) file of an accepted format that isn't itself damaged.
        var booksWithGoodCopy = siblings
            .Where(s => s.IntegrityOk != false
                && !IsUnderArchive(s.FullPath, archiveLeaf)
                && formats.Contains(FormatOf(s.FullPath) ?? ""))
            .Select(s => s.BookId!.Value)
            .ToHashSet();

        var warnings = new List<string>();
        int archived = 0, skipped = 0;
        foreach (var d in damaged)
        {
            ct.ThrowIfCancellationRequested();
            if (!booksWithGoodCopy.Contains(d.BookId!.Value)) { skipped++; continue; }
            var (ok, warning) = await ArchiveFileAsync(d, archiveLeaf, locations, ct);
            if (ok) archived++;
            else { skipped++; if (warning is not null) warnings.Add($"#{d.Id}: {warning}"); }
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new ArchiveReplacedResult(archived, skipped, warnings));
    }

    // Moves a file into the archive folder, preserving its library-relative
    // subpath (same rules as the Duplicates page). Updates file.FullPath; the
    // caller persists. Returns (false, reason) when the file is outside the
    // library roots, gone, or the move fails.
    private async Task<(bool ok, string? warning)> ArchiveFileAsync(
        LocalBookFile file, string archiveLeaf, IReadOnlyList<string> locations, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(file.FullPath))
            return (false, "File has no path recorded.");

        var location = locations.FirstOrDefault(l =>
            file.FullPath.StartsWith(l.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
        if (location is null) return (false, "File is outside the enabled library roots.");

        // Build the destination with forward slashes, NEVER Path.Combine: stored
        // paths are always forward-slash and the archive-exclusion filters match a
        // forward-slash prefix/segment. Path.Combine emits '\' on a Windows host,
        // which would store the row as ".../archive\Author\..." and make it silently
        // fail the exclusion (the archived copy then keeps showing). See the matching
        // note in BooksController.ApplyDuplicateAction.
        var libRoot = location.Replace('\\', '/').TrimEnd('/');
        var relative = file.FullPath.Replace('\\', '/')[libRoot.Length..].TrimStart('/');
        var destBase = archiveLeaf.Contains('/') || archiveLeaf.Contains('\\')
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
                // Forward-slash: UniqueFileAsync uses Path.Combine ('\' on Windows).
                var final = (await UniqueFileAsync(destPath, ct)).Replace('\\', '/');
                await _fs.MoveFileAsync(src, final, overwrite: false, ct);
                // Cross-mount File.Move is copy+delete; the source delete can silently
                // fail and leave the live original, which then reappears. Verify it's
                // gone, force-remove it, and never repoint the row while it survives.
                if (await _fs.FileExistsAsync(src, ct) && await _fs.FileExistsAsync(final, ct))
                {
                    try { await _fs.DeleteFileAsync(src, ct); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                if (await _fs.FileExistsAsync(src, ct))
                    return (false, $"Archived a copy but could not remove the live original at {src}.");
                file.FullPath = final;
            }
            else if (await _fs.DirectoryExistsAsync(file.FullPath, ct))
            {
                var src = file.FullPath;
                var final = (await UniqueDirectoryAsync(
                    Path.GetDirectoryName(destPath)!, Path.GetFileName(destPath), ct)).Replace('\\', '/');
                await _fs.MoveDirectoryAsync(src, final, ct);
                if (await _fs.DirectoryExistsAsync(src, ct))
                {
                    try { await _fs.DeleteDirectoryAsync(src, recursive: true, ct); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                if (await _fs.DirectoryExistsAsync(src, ct))
                    return (false, $"Archived a copy but could not remove the live original folder at {src}.");
                file.FullPath = final;
            }
            else return (false, "Path no longer exists on disk.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, ex.Message);
        }
        return (true, null);
    }

    private async Task<HashSet<string>> GetReplacementFormatsAsync(CancellationToken ct)
    {
        var raw = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.IntegrityReplacementFormats)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);
        return ParseFormats(string.IsNullOrWhiteSpace(raw) ? DefaultReplacementFormats : raw);
    }

    // Parses a ';'/','-separated format list into a normalised, case-insensitive set.
    public static HashSet<string> ParseFormats(string raw)
        => raw.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => f.TrimStart('.').ToLowerInvariant())
            .Where(f => f.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Re-queues every currently-damaged file for a fresh check (clears their
    /// stored fingerprints). Use after an integrity-checker fix to re-evaluate
    /// files that were flagged by the old logic but haven't changed on disk.
    /// POST /api/damaged/recheck-all
    /// </summary>
    [HttpPost("recheck-all")]
    public async Task<IActionResult> RecheckAll(CancellationToken ct)
    {
        var archiveLeaf = await GetArchiveLeafAsync(ct);
        var files = await _db.LocalBookFiles
            .Where(f => f.IntegrityOk == false)
            .Where(ArchivedFilesController.NotUnderArchive(archiveLeaf))
            .ToListAsync(ct);
        foreach (var f in files)
        {
            f.IntegrityCheckedSize = null;
            f.IntegrityCheckedModified = null;
            f.IntegrityOk = null;
            f.IntegrityError = null;
            f.IntegrityPages = null;
            f.IntegrityCheckedAt = null;
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new { requeued = files.Count });
    }

    /// <summary>
    /// Other files linked to the same book as this damaged file — possible
    /// replacements, including any copies that live in the archive folder.
    /// GET /api/damaged/{id}/alternates
    /// </summary>
    [HttpGet("{id:int}/alternates")]
    public async Task<ActionResult<IReadOnlyList<AlternateFile>>> GetAlternates(int id, CancellationToken ct)
    {
        var damaged = await _db.LocalBookFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct);
        if (damaged is null) return NotFound(new { error = "File not found." });
        if (damaged.BookId is null) return Array.Empty<AlternateFile>();

        var archiveLeaf = await GetArchiveLeafAsync(ct);

        var rows = await _db.LocalBookFiles.AsNoTracking()
            .Where(f => f.Id != id && f.BookId == damaged.BookId)
            .Select(f => new { f.Id, f.FullPath, f.SizeBytes, f.IntegrityOk })
            .ToListAsync(ct);

        return rows
            .Select(r => new AlternateFile(
                r.Id, r.FullPath, FormatOf(r.FullPath), r.SizeBytes,
                IsUnderArchive(r.FullPath, archiveLeaf), r.IntegrityOk))
            // Show good/archived candidates first; ties keep larger files up top.
            .OrderByDescending(a => a.Archived)
            .ThenByDescending(a => a.SizeBytes)
            .ToList();
    }

    /// <summary>
    /// Permanently removes a damaged file: deletes it from disk and drops the
    /// record. POST /api/damaged/{id}/remove
    /// </summary>
    [HttpPost("{id:int}/remove")]
    public async Task<IActionResult> Remove(int id, CancellationToken ct)
    {
        var file = await _db.LocalBookFiles.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (file is null) return NotFound(new { error = "File not found." });

        try
        {
            if (!string.IsNullOrWhiteSpace(file.FullPath))
            {
                if (await _fs.FileExistsAsync(file.FullPath, ct))
                    await _fs.DeleteFileAsync(file.FullPath, ct);
                else if (await _fs.DirectoryExistsAsync(file.FullPath, ct))
                    await _fs.DeleteDirectoryAsync(file.FullPath, recursive: true, ct);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Ok(new { removed = false, warning = ex.Message });
        }

        _db.LocalBookFiles.Remove(file);
        await _db.SaveChangesAsync(ct);
        return Ok(new { removed = true });
    }

    /// <summary>
    /// Replaces a damaged file with another copy (typically an archived one):
    /// moves the replacement into the damaged file's folder, re-queues it for an
    /// integrity check, then removes the damaged file from disk + DB.
    /// POST /api/damaged/{id}/replace-with/{replacementId}
    /// </summary>
    [HttpPost("{id:int}/replace-with/{replacementId:int}")]
    public async Task<IActionResult> ReplaceWith(int id, int replacementId, CancellationToken ct)
    {
        if (id == replacementId) return BadRequest(new { error = "A file can't replace itself." });

        var damaged = await _db.LocalBookFiles.FirstOrDefaultAsync(f => f.Id == id, ct);
        var replacement = await _db.LocalBookFiles.FirstOrDefaultAsync(f => f.Id == replacementId, ct);
        if (damaged is null || replacement is null)
            return NotFound(new { error = "File not found." });
        if (damaged.BookId is null || replacement.BookId != damaged.BookId)
            return BadRequest(new { error = "The replacement must be linked to the same book." });

        // Restore the replacement into the damaged file's folder (out of the
        // archive). Fall back to the replacement's own folder if the damaged
        // path has no directory part.
        var destDir = Path.GetDirectoryName(damaged.FullPath);
        if (string.IsNullOrWhiteSpace(destDir))
            destDir = Path.GetDirectoryName(replacement.FullPath);
        if (string.IsNullOrWhiteSpace(destDir))
            return BadRequest(new { error = "Could not determine a destination folder." });

        try
        {
            await _fs.CreateDirectoryAsync(destDir, ct);

            // Move the replacement in first, so a failure leaves the damaged
            // file untouched and the book still represented.
            if (await _fs.FileExistsAsync(replacement.FullPath, ct))
            {
                var dest = await UniqueFileAsync(Path.Combine(destDir, Path.GetFileName(replacement.FullPath)), ct);
                await _fs.MoveFileAsync(replacement.FullPath, dest, overwrite: false, ct);
                replacement.FullPath = dest;
            }
            else if (await _fs.DirectoryExistsAsync(replacement.FullPath, ct))
            {
                var dest = await UniqueDirectoryAsync(destDir, Path.GetFileName(replacement.FullPath.TrimEnd('/', '\\')), ct);
                await _fs.MoveDirectoryAsync(replacement.FullPath, dest, ct);
                replacement.FullPath = dest;
            }
            else
            {
                return Ok(new { replaced = false, warning = "Replacement file no longer exists on disk." });
            }

            // The restored copy is a fresh library file — re-evaluate it.
            replacement.IntegrityCheckedSize = null;
            replacement.IntegrityCheckedModified = null;
            replacement.IntegrityOk = null;
            replacement.IntegrityError = null;
            replacement.IntegrityPages = null;
            replacement.IntegrityCheckedAt = null;

            // Now delete the damaged file.
            if (await _fs.FileExistsAsync(damaged.FullPath, ct))
                await _fs.DeleteFileAsync(damaged.FullPath, ct);
            else if (await _fs.DirectoryExistsAsync(damaged.FullPath, ct))
                await _fs.DeleteDirectoryAsync(damaged.FullPath, recursive: true, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _db.SaveChangesAsync(ct); // persist any move that did land
            return Ok(new { replaced = false, warning = ex.Message });
        }

        _db.LocalBookFiles.Remove(damaged);
        await _db.SaveChangesAsync(ct);
        return Ok(new { replaced = true, newPath = replacement.FullPath });
    }

    /// <summary>Starts the integrity job now. POST /api/damaged/run</summary>
    [HttpPost("run")]
    public IActionResult Run()
    {
        if (!_integrity.TryStart(_lifetime.ApplicationStopping, out var error))
            return Conflict(new { error });
        return Ok(new { started = true });
    }

    // In-memory equivalent of ArchivedFilesController's archive match: true when
    // the (forward-slash) path sits under the archive folder, whether that's a
    // simple leaf name or a full absolute path.
    private static bool IsUnderArchive(string fullPath, string archiveLeaf)
    {
        if (string.IsNullOrEmpty(fullPath)) return false;
        var leaf = archiveLeaf.Replace('\\', '/').TrimEnd('/');
        var p = fullPath.Replace('\\', '/');
        return leaf.Contains('/')
            ? p.StartsWith(leaf + "/", StringComparison.OrdinalIgnoreCase)
            : p.Contains("/" + leaf + "/", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> GetArchiveLeafAsync(CancellationToken ct)
    {
        var stored = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.DedupeArchiveFolder)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(stored) ? "__archive" : stored.Trim();
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

    private static string? FormatOf(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return null;
        var ext = Path.GetExtension(fullPath).TrimStart('.').ToLowerInvariant();
        return string.IsNullOrEmpty(ext) ? null : ext;
    }
}

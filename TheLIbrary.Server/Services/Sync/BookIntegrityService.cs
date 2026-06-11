using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.IO;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

public sealed record BookIntegritySummary(int Checked, int Ok, int Damaged, int Skipped);

// Scheduled job: opens (or converts via Calibre) each matched ebook file and
// verifies it has at least BookIntegrityChecker.MinPages pages. Files that
// can't be opened/converted, or are too short, are flagged damaged and surface
// on the Damaged page.
//
// Incremental + cheap to re-run: a file is only (re)checked when its folder
// fingerprint (LocalBookFile.SizeBytes) differs from the value stored at the
// last check, or it has never been checked. That comparison is DB-only, so the
// candidate scan does no disk I/O — each run then processes at most
// MaxBooksPerRun files, working through the backlog over successive runs.
public sealed class BookIntegrityService
{
    public const int DefaultMaxBooksPerRun = 200;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly BookIntegrityChecker _checker;
    private readonly ContentScanService _contentScan;
    private readonly IFileSystem _fs;
    private readonly ILogger<BookIntegrityService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;
    private BookIntegritySummary? _lastResult;

    public BookIntegrityService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        BookIntegrityChecker checker,
        ContentScanService contentScan,
        IFileSystem fs,
        ILogger<BookIntegrityService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _checker = checker;
        _contentScan = contentScan;
        _fs = fs;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;
    public BookIntegritySummary? LastResult => _lastResult;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("check book integrity", out var holder))
        {
            error = $"Another task is already running ({holder})";
            return false;
        }

        error = null;
        _isRunning = true;
        _ = Task.Run(async () =>
        {
            try { _lastResult = await RunAsync(hostCt); }
            catch (OperationCanceledException) when (hostCt.IsCancellationRequested) { }
            catch (Exception ex) { _log.LogError(ex, "Book-integrity job failed"); }
            finally { _isRunning = false; _currentMessage = null; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    // Test seam: runs the body synchronously (no coordinator / background task)
    // so assertions can inspect the resulting DB state deterministically.
    internal Task<BookIntegritySummary> RunForTestsAsync(CancellationToken ct) => RunAsync(ct);

    private async Task<BookIntegritySummary> RunAsync(CancellationToken ct)
    {
        _log.LogInformation("Book-integrity job starting");
        _currentMessage = "Loading files to check";
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

        var max = await LoadMaxBooksPerRunAsync(db, ct);
        var archiveLeaf = await LoadArchiveLeafAsync(db, ct);
        var locations = await db.LibraryLocations.AsNoTracking()
            .Where(l => l.Enabled).Select(l => l.Path).ToListAsync(ct);

        // Self-heal: clear the damaged flag on any flagged row that isn't an ebook
        // FILE — a directory / extensionless row an older check wrongly flagged.
        // This job only ever checks ebook-extension paths, so such rows can never be
        // re-evaluated and would otherwise sit on the Damaged page forever as
        // un-previewable ". files".
        var flagged = await db.LocalBookFiles.AsNoTracking()
            .Where(f => f.IntegrityOk == false)
            .Select(f => new { f.Id, f.FullPath })
            .ToListAsync(ct);
        var stuckIds = flagged.Where(f => !BookIntegrityChecker.IsEbook(f.FullPath)).Select(f => f.Id).ToList();
        if (stuckIds.Count > 0)
        {
            var toReset = await db.LocalBookFiles.Where(f => stuckIds.Contains(f.Id)).ToListAsync(ct);
            foreach (var f in toReset)
            {
                f.IntegrityOk = null;
                f.IntegrityError = null;
                f.IntegrityPages = null;
            }
            await db.SaveChangesAsync(ct);
            _log.LogInformation("Book-integrity: cleared {Count} stuck non-file damaged row(s)", toReset.Count);
        }

        // Candidate = an ebook file linked to a book OR (unmatched but) to an
        // author, whose fingerprint changed since — or was never — checked. The
        // ebook-extension filter and the size/modified comparison run in SQL so
        // we never materialise the whole LocalBookFiles table. A file is
        // re-checked only when its size OR modified time differs from what was
        // stored at the last check (so a file marked OK stays OK until it
        // actually changes on disk).
        var baseQuery = db.LocalBookFiles
            .Where(f => (f.BookId != null || f.AuthorId != null)
                && (f.IntegrityCheckedSize == null
                    || f.IntegrityCheckedSize != f.SizeBytes
                    || f.IntegrityCheckedModified != f.ModifiedAt)
                && (f.FullPath.EndsWith(".epub") || f.FullPath.EndsWith(".pdf")
                    || f.FullPath.EndsWith(".mobi") || f.FullPath.EndsWith(".azw")
                    || f.FullPath.EndsWith(".azw3") || f.FullPath.EndsWith(".fb2")
                    || f.FullPath.EndsWith(".cbz") || f.FullPath.EndsWith(".cbr")
                    || f.FullPath.EndsWith(".lit") || f.FullPath.EndsWith(".djvu")
                    || f.FullPath.EndsWith(".doc") || f.FullPath.EndsWith(".docx")
                    || f.FullPath.EndsWith(".rtf") || f.FullPath.EndsWith(".txt")));

        // Priority order: unarchived before archived (an archived copy is already
        // out of the live library, so its health matters least), and within each
        // group starred authors (higher Author.Priority) first. Net effect:
        //   1. starred + unarchived   2. unarchived
        //   3. starred + archived     4. archived
        // Ties fall back to Id so progress through the backlog is deterministic.
        // The archived test runs in SQL — branch in C# so EF emits the right form
        // for a leaf-name vs full-path archive folder.
        IOrderedQueryable<LocalBookFile> ordered;
        if (archiveLeaf.Contains('/'))
        {
            var prefix = archiveLeaf + "/";
            ordered = baseQuery.OrderBy(f => f.FullPath.StartsWith(prefix) ? 1 : 0);
        }
        else
        {
            var segment = "/" + archiveLeaf + "/";
            ordered = baseQuery.OrderBy(f => f.FullPath.Contains(segment) ? 1 : 0);
        }

        var candidates = await ordered
            // Starred authors first; null-safe for unmatched files that have an
            // author link but no book.
            .ThenByDescending(f => f.Book != null ? f.Book.Author.Priority
                                 : f.Author != null ? f.Author.Priority : 0)
            .ThenBy(f => f.Id)
            .Take(max)
            // Book + Author are read for the live progress message.
            .Include(f => f.Book!).ThenInclude(b => b.Author)
            .Include(f => f.Author)
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            // All author-linked files are done — move on to the untracked files
            // sitting in the __unknown bucket (the UnknownFiles index).
            return await RunUntrackedPhaseAsync(db, max, archiveLeaf, locations, ct);
        }

        // Whenever the run touches a file that belongs to a book, fold in EVERY
        // other still-unchecked file of that same book/title — even if that pushes
        // the run past `max`. Checking all copies of a title together is what lets
        // the Duplicates page pick a healthy keeper, so it's worth the extra work.
        var candidateIds = candidates.Select(c => c.Id).ToHashSet();
        var bookIds = candidates.Where(c => c.BookId.HasValue).Select(c => c.BookId!.Value).Distinct().ToList();
        if (bookIds.Count > 0)
        {
            var siblings = await baseQuery
                .Where(f => f.BookId.HasValue && bookIds.Contains(f.BookId.Value))
                .Include(f => f.Book!).ThenInclude(b => b.Author)
                .Include(f => f.Author)
                .ToListAsync(ct);
            foreach (var s in siblings)
                if (candidateIds.Add(s.Id)) candidates.Add(s);
        }

        int checkedCount = 0, ok = 0, damaged = 0, skipped = 0, archivedOrphans = 0;

        for (var i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = candidates[i];

            var author = file.Book?.Author?.Name ?? file.Author?.Name ?? file.AuthorFolder;
            var title = file.Book?.Title ?? file.TitleFolder;
            var format = Path.GetExtension(file.FullPath).TrimStart('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(format)) format = "?";
            _currentMessage = $"Checking {i + 1}/{candidates.Count}: {author} — {title} ({format})";

            var result = await _checker.CheckAsync(file.FullPath, ct);
            if (result.Status == IntegrityStatus.Skipped)
            {
                // Leave the record untouched so it's retried once the blocker
                // (e.g. Calibre not configured) is resolved.
                skipped++;
                continue;
            }

            var isDamaged = result.Status != IntegrityStatus.Ok;
            file.IntegrityOk = !isDamaged;
            // Cap to the column length — an uncapped converter/parser message
            // would fail SaveChanges and (before per-file persistence) threw
            // away the whole run's progress.
            file.IntegrityError = isDamaged ? Cap(result.Error, 1000) : null;
            file.IntegrityPages = result.Pages;
            file.IntegrityCheckedSize = file.SizeBytes;
            file.IntegrityCheckedModified = file.ModifiedAt;
            file.IntegrityCheckedAt = DateTime.UtcNow;

            checkedCount++;
            if (isDamaged) damaged++; else ok++;

            // The file is open and healthy right now — fold the content check in
            // while it's warm rather than re-reading it in a later content-scan
            // run. Harvests the series catalogue from matched books and the full
            // guess from unmatched ones; best-effort, never breaks the run.
            if (!isDamaged)
                await _contentScan.ExtractDuringIntegrityAsync(db, file, ct);

            // A damaged file that's unmatched (no book) but linked to an author
            // is just a bad orphan — there's no book to triage it against on the
            // Damaged page, so archive it straight away rather than leave it.
            if (isDamaged && file.BookId == null && file.AuthorId != null
                && !IsUnderArchive(file.FullPath, archiveLeaf))
            {
                var moved = await TryArchiveAsync(file.FullPath, archiveLeaf, locations, ct);
                if (moved is not null) { file.FullPath = moved; archivedOrphans++; }
            }

            // Persist PER FILE: a single check can take minutes (a .lit/.mobi
            // conversion runs up to the 10-minute Calibre timeout), so batching
            // meant a restart/redeploy/cancel mid-batch lost every result since
            // the last boundary — and the next run re-picked the exact same
            // candidates ("processing the same books each time"). A row that
            // can't be saved is detached so it can't poison the rest of the run.
            try { await db.SaveChangesAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Book-integrity: could not persist result for {Path}", file.FullPath);
                db.Entry(file).State = EntityState.Detached;
            }
        }

        await db.SaveChangesAsync(ct);

        _log.LogInformation(
            "Book-integrity job done — checked {Checked}, ok {Ok}, damaged {Damaged}, skipped {Skipped}, archived orphans {Archived}",
            checkedCount, ok, damaged, skipped, archivedOrphans);
        _currentMessage = archivedOrphans > 0
            ? $"Done — checked {checkedCount}, damaged {damaged} ({archivedOrphans} orphan(s) archived)"
            : $"Done — checked {checkedCount}, damaged {damaged}";
        return new BookIntegritySummary(checkedCount, ok, damaged, skipped);
    }

    private static bool IsUnderArchive(string fullPath, string archiveLeaf)
    {
        if (string.IsNullOrEmpty(fullPath)) return false;
        var p = fullPath.Replace('\\', '/');
        return archiveLeaf.Contains('/')
            ? p.StartsWith(archiveLeaf + "/", StringComparison.OrdinalIgnoreCase)
            : p.Contains("/" + archiveLeaf + "/", StringComparison.OrdinalIgnoreCase);
    }

    // Moves a file into the archive folder, preserving its library-relative
    // subpath (same rules as the Duplicates / Damaged archive). Updates
    // file.FullPath; the caller persists. Best-effort — returns false on any
    // problem so a failed move never aborts the run.
    // Moves a file/dir at `fullPath` into the archive folder, preserving its
    // library-relative subpath. Returns the new path, or null when it's outside
    // the library roots, gone, or the move failed. Best-effort.
    private async Task<string?> TryArchiveAsync(
        string fullPath, string archiveLeaf, IReadOnlyList<string> locations, CancellationToken ct)
    {
        try
        {
            var location = locations.FirstOrDefault(l =>
                fullPath.StartsWith(l.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
            if (location is null) return null;

            var libRoot = location.TrimEnd('\\', '/');
            var relative = fullPath[libRoot.Length..].TrimStart('\\', '/');
            var destBase = archiveLeaf.Contains('/') || archiveLeaf.Contains('\\')
                ? archiveLeaf.Replace('\\', '/')
                : Path.Combine(libRoot, archiveLeaf);
            var destPath = Path.Combine(destBase, relative);
            var destDir = Path.GetDirectoryName(destPath);
            if (destDir is not null) await _fs.CreateDirectoryAsync(destDir, ct);

            if (await _fs.FileExistsAsync(fullPath, ct))
            {
                var final = await UniqueAsync(destPath, ct);
                await _fs.MoveFileAsync(fullPath, final, overwrite: false, ct);
                return final;
            }
            if (await _fs.DirectoryExistsAsync(fullPath, ct))
            {
                var final = await UniqueAsync(destPath, ct);
                await _fs.MoveDirectoryAsync(fullPath, final, ct);
                return final;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Book-integrity: failed to archive {Path}", fullPath);
            return null;
        }
    }

    private async Task<string> UniqueAsync(string desired, CancellationToken ct)
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

    // Phase 2: only reached once every author-linked file is done. Checks the
    // untracked files in the __unknown bucket (UnknownFiles index); a damaged one
    // is archived (author unknown — the move just preserves its library-relative
    // path). Healthy ones are recorded in UnknownFileChecks so they aren't
    // re-checked every run (that table survives the UnknownFiles re-index).
    private async Task<BookIntegritySummary> RunUntrackedPhaseAsync(
        LibraryDbContext db, int max, string archiveLeaf, IReadOnlyList<string> locations, CancellationToken ct)
    {
        var candidates = await db.UnknownFiles.AsNoTracking()
            .Where(u => (u.FullPath.EndsWith(".epub") || u.FullPath.EndsWith(".pdf")
                || u.FullPath.EndsWith(".mobi") || u.FullPath.EndsWith(".azw")
                || u.FullPath.EndsWith(".azw3") || u.FullPath.EndsWith(".fb2")
                || u.FullPath.EndsWith(".cbz") || u.FullPath.EndsWith(".cbr")
                || u.FullPath.EndsWith(".lit") || u.FullPath.EndsWith(".djvu")
                || u.FullPath.EndsWith(".doc") || u.FullPath.EndsWith(".docx")
                || u.FullPath.EndsWith(".rtf") || u.FullPath.EndsWith(".txt"))
                && !db.UnknownFileChecks.Any(c => c.FullPath == u.FullPath
                    && c.SizeBytes == u.SizeBytes && c.ModifiedAt == u.ModifiedAt))
            .OrderBy(u => u.Id)
            .Take(max)
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            _log.LogInformation("Book-integrity job: nothing new to check (author-linked + untracked all done)");
            _currentMessage = "Done — nothing to check";
            return new BookIntegritySummary(0, 0, 0, 0);
        }

        int checkedCount = 0, ok = 0, damaged = 0, skipped = 0, archived = 0;

        for (var i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var uf = candidates[i];
            var format = Path.GetExtension(uf.FullPath).TrimStart('.').ToLowerInvariant();
            _currentMessage = $"Checking untracked {i + 1}/{candidates.Count}: {uf.FileName} ({(format.Length > 0 ? format : "?")})";

            var result = await _checker.CheckAsync(uf.FullPath, ct);
            if (result.Status == IntegrityStatus.Skipped) { skipped++; continue; }

            checkedCount++;
            if (result.Status == IntegrityStatus.Ok)
            {
                ok++;
                await RecordUntrackedCheckAsync(db, uf, ct); // healthy → don't re-check
            }
            else
            {
                damaged++;
                var moved = await TryArchiveAsync(uf.FullPath, archiveLeaf, locations, ct);
                if (moved is not null)
                {
                    archived++;
                    // It left __unknown; drop any stale check rows for the old path.
                    var stale = await db.UnknownFileChecks.Where(c => c.FullPath == uf.FullPath).ToListAsync(ct);
                    if (stale.Count > 0) db.UnknownFileChecks.RemoveRange(stale);
                }
                else
                {
                    // Couldn't archive (outside library roots / gone) — record the
                    // check so we don't keep re-evaluating it every run.
                    await RecordUntrackedCheckAsync(db, uf, ct);
                }
            }

            // Per-file persistence, same reasoning as the author-linked phase:
            // progress must survive a cancelled/killed run. Candidates here are
            // no-tracking, so clearing the tracker on a failed save is safe.
            try { await db.SaveChangesAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Book-integrity: could not persist untracked result for {Path}", uf.FullPath);
                db.ChangeTracker.Clear();
            }
        }

        await db.SaveChangesAsync(ct);
        _log.LogInformation(
            "Book-integrity untracked phase done — checked {Checked}, ok {Ok}, damaged {Damaged} ({Archived} archived), skipped {Skipped}",
            checkedCount, ok, damaged, archived, skipped);
        _currentMessage = $"Done — checked {checkedCount} untracked, {archived} damaged archived";
        return new BookIntegritySummary(checkedCount, ok, damaged, skipped);
    }

    private static async Task RecordUntrackedCheckAsync(LibraryDbContext db, UnknownFile uf, CancellationToken ct)
    {
        var existing = await db.UnknownFileChecks.FirstOrDefaultAsync(c => c.FullPath == uf.FullPath, ct);
        if (existing is null)
            db.UnknownFileChecks.Add(new UnknownFileCheck
            {
                FullPath = uf.FullPath,
                SizeBytes = uf.SizeBytes,
                ModifiedAt = uf.ModifiedAt,
                CheckedAt = DateTime.UtcNow,
            });
        else
        {
            existing.SizeBytes = uf.SizeBytes;
            existing.ModifiedAt = uf.ModifiedAt;
            existing.CheckedAt = DateTime.UtcNow;
        }
    }

    private static string? Cap(string? s, int max)
        => s is null ? null : s.Length <= max ? s : s[..max];

    private static async Task<int> LoadMaxBooksPerRunAsync(LibraryDbContext db, CancellationToken ct)
    {
        var raw = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.IntegrityMaxBooksPerRun)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        return int.TryParse(raw, out var n) && n > 0 ? n : DefaultMaxBooksPerRun;
    }

    // The dedupe archive folder (leaf name or absolute path), forward-slash
    // normalised. Used only to rank archived copies last in the candidate scan.
    private static async Task<string> LoadArchiveLeafAsync(LibraryDbContext db, CancellationToken ct)
    {
        var raw = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.DedupeArchiveFolder)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        return (string.IsNullOrWhiteSpace(raw) ? "__archive" : raw.Trim())
            .Replace('\\', '/').TrimEnd('/');
    }
}

using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.IO;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

public sealed record StaleFileCleanupSummary(int Scanned, int Pruned, int EmptyFoldersRemoved = 0);

// Removes leftover "folder pointer" LocalBookFile rows: rows whose FullPath is a
// directory (classic Calibre layout) that no longer holds a readable ebook —
// usually a title folder left behind after its file was moved or archived. These
// otherwise surface on the Duplicates page as a bare-folder "copy".
//
// NAS-safe by construction:
//   * Only ever considers folder-shaped paths (no ebook extension) — a real file
//     row (book.epub) is never touched, so a transient mount glitch can't delete
//     genuine files.
//   * Skips any row whose library root isn't currently mounted, so a whole-mount
//     outage can't be mistaken for "everything is stale".
public sealed class StaleFileCleanupService
{
    // Extensions that mark a path as a file rather than a folder. A row ending in
    // one of these is skipped outright (never a "folder pointer").
    private static readonly string[] FileExtensions =
    {
        ".epub", ".pdf", ".mobi", ".azw", ".azw3", ".azw4", ".kf8", ".prc", ".pdb",
        ".fb2", ".fbz", ".lit", ".cbz", ".cbr", ".docx", ".odt", ".rtf", ".txt",
        ".opf", ".zip", ".rar", ".djvu", ".doc",
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly IFileSystem _fs;
    private readonly ILogger<StaleFileCleanupService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;
    private StaleFileCleanupSummary? _lastResult;

    public StaleFileCleanupService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        IFileSystem fs,
        ILogger<StaleFileCleanupService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _fs = fs;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;
    public StaleFileCleanupSummary? LastResult => _lastResult;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("prune stale folder records", out var holder))
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
            catch (Exception ex) { _log.LogError(ex, "Stale-file cleanup job failed"); }
            finally { _isRunning = false; _currentMessage = null; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    internal Task<StaleFileCleanupSummary> RunForTestsAsync(CancellationToken ct) => RunAsync(ct);

    private async Task<StaleFileCleanupSummary> RunAsync(CancellationToken ct)
    {
        _log.LogInformation("Stale-file cleanup starting");
        _currentMessage = "Loading library roots";
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

        var roots = await db.LibraryLocations.AsNoTracking()
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);

        // Only operate under roots that are actually mounted right now. If none
        // are reachable, abort rather than risk pruning everything.
        var mountedRoots = roots
            .Select(r => r.TrimEnd('\\', '/'))
            .Where(r => r.Length > 0 && _fs.DirectoryExists(r))
            .ToList();
        if (mountedRoots.Count == 0)
        {
            _log.LogWarning("Stale-file cleanup: no library root is mounted — skipping run");
            _currentMessage = "Skipped — no library root mounted";
            return new StaleFileCleanupSummary(0, 0);
        }

        // Candidates = rows whose path doesn't end with a known file extension,
        // i.e. folder-shaped pointers. Filtered in SQL so the whole table is
        // never materialised.
        var query = db.LocalBookFiles.AsQueryable();
        foreach (var ext in FileExtensions)
        {
            var e = ext; // capture
            query = query.Where(f => !f.FullPath.EndsWith(e));
        }
        var candidates = await query
            .Select(f => new { f.Id, f.FullPath })
            .ToListAsync(ct);

        var scanned = 0;
        var toRemove = new List<int>();
        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();
            scanned++;
            if (string.IsNullOrWhiteSpace(c.FullPath)) continue;

            var root = mountedRoots.FirstOrDefault(r =>
                c.FullPath.StartsWith(r, StringComparison.OrdinalIgnoreCase));
            if (root is null) continue; // outside a mounted root — leave it alone

            // A real file (with an odd extension) at this path → keep.
            if (_fs.FileExists(c.FullPath)) continue;

            if (_fs.DirectoryExists(c.FullPath))
            {
                // Folder that still holds a readable ebook → a genuine copy, keep.
                if (FolderHoldsEbook(c.FullPath)) continue;
            }
            // Empty folder, or a folder that no longer exists under a mounted
            // root → a stale pointer. Prune it.
            toRemove.Add(c.Id);
        }

        if (toRemove.Count > 0)
        {
            _currentMessage = $"Pruning {toRemove.Count} stale folder record(s)";
            // Delete in chunks to keep the SQL parameter count sane.
            foreach (var chunk in toRemove.Chunk(500))
            {
                ct.ThrowIfCancellationRequested();
                var rows = await db.LocalBookFiles.Where(f => chunk.Contains(f.Id)).ToListAsync(ct);
                db.LocalBookFiles.RemoveRange(rows);
                await db.SaveChangesAsync(ct);
            }
        }

        // Second pass: empty folders should never linger on disk. After files are
        // moved (organize, dedupe, archive, assign) or deleted, their title and
        // author folders are routinely left behind empty — clutter that also
        // produces the folder-shaped rows pruned above on the next sync. Remove
        // every recursively-empty directory under each mounted root, bottom-up.
        // Only ever deletes a directory that contains NO files anywhere beneath
        // it, so no book or cover is ever at risk.
        _currentMessage = "Removing empty folders";
        var protect = await BuildProtectedSetAsync(db, mountedRoots, ct);
        var emptyRemoved = 0;
        foreach (var root in mountedRoots)
        {
            ct.ThrowIfCancellationRequested();
            emptyRemoved += RemoveEmptyDirectories(root, protect, ct);
        }

        _log.LogInformation(
            "Stale-file cleanup done — scanned {Scanned}, pruned {Pruned} row(s), removed {Empty} empty folder(s)",
            scanned, toRemove.Count, emptyRemoved);
        _currentMessage = $"Done — pruned {toRemove.Count} of {scanned} folder record(s), removed {emptyRemoved} empty folder(s)";
        return new StaleFileCleanupSummary(scanned, toRemove.Count, emptyRemoved);
    }

    // Absolute directory paths that must never be deleted even when empty: the
    // mounted library roots themselves plus the configured quarantine / archive /
    // incoming folders (and the per-location <root>/__unknown default). Deleting
    // and recreating these would be pointless churn and could surprise the user.
    private async Task<HashSet<string>> BuildProtectedSetAsync(
        LibraryDbContext db, IReadOnlyList<string> mountedRoots, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? p) { if (!string.IsNullOrWhiteSpace(p)) set.Add(p.Replace('\\', '/').TrimEnd('/')); }

        foreach (var r in mountedRoots)
        {
            Add(r);
            Add(r + "/" + CalibreScanner.UnknownAuthorFolder);
        }

        var settings = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.UnknownFolder
                     || s.Key == AppSettingKeys.IncomingFolder
                     || s.Key == AppSettingKeys.DedupeArchiveFolder)
            .ToListAsync(ct);
        foreach (var s in settings)
        {
            var v = s.Value?.Trim();
            if (string.IsNullOrWhiteSpace(v)) continue;
            // Archive may be stored as a leaf name rather than an absolute path —
            // protect both the absolute form and the per-root leaf.
            if (s.Key == AppSettingKeys.DedupeArchiveFolder && !v.Contains('/') && !v.Contains('\\'))
            {
                foreach (var r in mountedRoots) Add(r + "/" + v);
            }
            else Add(v);
        }
        return set;
    }

    // Recursively removes empty directories under `root`, bottom-up. Returns the
    // count removed. `root` itself is never removed. A directory is removed only
    // when it has no file-system entries left after its children are processed;
    // unreadable directories are treated as non-empty (never deleted).
    private int RemoveEmptyDirectories(string root, HashSet<string> protect, CancellationToken ct)
    {
        var removed = 0;

        // Returns true when `dir` is now empty (and so a deletion candidate).
        bool Walk(string dir)
        {
            ct.ThrowIfCancellationRequested();
            List<string> subdirs;
            try { subdirs = _fs.EnumerateDirectories(dir).ToList(); }
            catch { return false; } // can't read → assume non-empty, never delete

            foreach (var sub in subdirs)
            {
                if (!Walk(sub)) continue;            // child still has content
                var norm = sub.Replace('\\', '/').TrimEnd('/');
                if (protect.Contains(norm)) continue; // protected, leave it
                try { _fs.DeleteDirectory(sub); removed++; }
                catch { /* race / permission — skip, retried next run */ }
            }

            try { return !_fs.EnumerateFileSystemEntries(dir).Any(); }
            catch { return false; }
        }

        Walk(root); // evaluate the root's children; never delete the root itself
        return removed;
    }

    private bool FolderHoldsEbook(string folder)
    {
        try
        {
            return _fs.EnumerateFiles(folder)
                .Any(f => CalibreScanner.EbookExtensions.Contains(Path.GetExtension(f)));
        }
        catch
        {
            // Can't read the folder — be conservative and treat it as non-empty
            // (don't prune) so a transient read error never deletes a row.
            return true;
        }
    }
}

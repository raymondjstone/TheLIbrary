using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

public sealed record AuthorDuplicateRemovalSummary(
    int AuthorFoldersScanned,
    int FilesScanned,
    int DuplicateGroups,
    int FilesDeleted,
    int EmptyFilesDeleted,
    long BytesFreed,
    int DbRowsRemoved);

// Scheduled job (daily): for every author that has unmatched files, deletes
// byte-identical duplicate copies WITHIN that author's own folder, keeping one.
//
// Scope is strictly per author folder — each folder is scanned in isolation, so
// two different authors who happen to hold the same file are NEVER compared and
// nothing crosses an author boundary. The duplicate determination is exactly the
// shared ContentDuplicateScanner (size-group → SHA-256 → one keeper; zero-byte
// files deleted as junk) used by the __unknown dedupe job.
//
// Keeper preference: a copy that's linked to a Book wins over an unlinked one
// (so a matched book never loses its only file), then shortest path. DB rows
// (LocalBookFiles, BookContentScans) for deleted paths are pruned.
//
// NAS-safe: aborts if no library root is mounted, and never deletes anything
// outside a mounted author folder.
public sealed class AuthorDuplicateRemovalService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly ILogger<AuthorDuplicateRemovalService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;
    private AuthorDuplicateRemovalSummary? _lastResult;

    public AuthorDuplicateRemovalService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        ILogger<AuthorDuplicateRemovalService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;
    public AuthorDuplicateRemovalSummary? LastResult => _lastResult;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("dedupe author files", out var holder))
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
            catch (Exception ex)
            {
                _log.LogError(ex, "Dedupe author files job failed");
                _currentMessage = $"Failed: {ex.Message}";
            }
            finally { _isRunning = false; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    internal Task<AuthorDuplicateRemovalSummary> RunForTestsAsync(CancellationToken ct) => RunAsync(ct);

    private async Task<AuthorDuplicateRemovalSummary> RunAsync(CancellationToken ct)
    {
        _log.LogInformation("Dedupe author files job starting");
        _currentMessage = "Loading authors with unmatched files";
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

        var roots = (await db.LibraryLocations.AsNoTracking()
                .Where(l => l.Enabled).Select(l => l.Path).ToListAsync(ct))
            .Select(r => r.TrimEnd('\\', '/'))
            .Where(r => r.Length > 0)
            .ToList();
        var mountedRoots = roots.Where(Directory.Exists).ToList();
        if (mountedRoots.Count == 0)
        {
            _log.LogWarning("Dedupe author files: no library root is mounted — skipping run");
            _currentMessage = "Skipped — no library root mounted";
            return new AuthorDuplicateRemovalSummary(0, 0, 0, 0, 0, 0, 0);
        }

        // Authors that have at least one unmatched file → the folders to dedupe.
        // We locate each author's folder from the file paths themselves (robust
        // to a stale CalibreFolderName), then dedupe the WHOLE folder on disk.
        // Archived files are inert (see ArchivePolicy) — never feed an archived
        // folder into the byte-identical dedupe, which would delete archived copies.
        var archiveLeaf = await ArchivePolicy.LoadLeafAsync(db, ct);
        var unmatchedAuthorIds = await db.LocalBookFiles.AsNoTracking()
            .Where(f => f.AuthorId != null && f.BookId == null)
            .Where(ArchivePolicy.NotUnder(archiveLeaf))
            .Select(f => f.AuthorId!.Value)
            .Distinct()
            .ToListAsync(ct);
        if (unmatchedAuthorIds.Count == 0)
        {
            _currentMessage = "Done — no authors with unmatched files";
            return new AuthorDuplicateRemovalSummary(0, 0, 0, 0, 0, 0, 0);
        }

        // Every author-folder directory belonging to those authors (derived from
        // their files' paths). Distinct, so a folder is scanned at most once.
        var authorFolderDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in unmatchedAuthorIds.Chunk(500))
        {
            ct.ThrowIfCancellationRequested();
            var ids = chunk.ToList();
            var paths = await db.LocalBookFiles.AsNoTracking()
                .Where(f => f.AuthorId != null && ids.Contains(f.AuthorId!.Value))
                .Where(ArchivePolicy.NotUnder(archiveLeaf))
                .Select(f => f.FullPath)
                .ToListAsync(ct);
            foreach (var p in paths)
            {
                var dir = AuthorFolderDir(p, mountedRoots);
                if (dir is not null) authorFolderDirs.Add(dir);
            }
        }

        var walkOpts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        int foldersScanned = 0, filesScanned = 0, groups = 0, deleted = 0, emptyDeleted = 0, dbRows = 0;
        long bytesFreed = 0;

        foreach (var dir in authorFolderDirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(dir)) continue;
            foldersScanned++;
            _currentMessage = $"Folder {foldersScanned}/{authorFolderDirs.Count}: {Path.GetFileName(dir)}";

            // Materialise this folder's files so we can compute keeper preference
            // and dedupe in isolation — nothing here ever sees another folder.
            List<string> files;
            try { files = Directory.EnumerateFiles(dir, "*", walkOpts).ToList(); }
            catch (Exception ex) { _log.LogWarning(ex, "Dedupe author files: could not enumerate {Dir}", dir); continue; }
            if (files.Count == 0) continue;

            // Prefer keeping a copy that's linked to a Book so a matched book
            // never loses its only file to a duplicate deletion.
            var preferKeep = await LinkedPathsAmongAsync(db, files, ct);

            var scan = await ContentDuplicateScanner.ScanAndDeleteAsync(
                files, _log, msg => _currentMessage = $"{Path.GetFileName(dir)}: {msg}", ct, preferKeep);

            filesScanned += scan.FilesScanned;
            groups += scan.DuplicateGroups;
            deleted += scan.FilesDeleted;
            emptyDeleted += scan.EmptyFilesDeleted;
            bytesFreed += scan.BytesFreed;

            dbRows += await PruneDbRowsAsync(db, scan.DeletedPaths, ct);
        }

        var summary = new AuthorDuplicateRemovalSummary(
            foldersScanned, filesScanned, groups, deleted, emptyDeleted, bytesFreed, dbRows);
        _log.LogInformation(
            "Dedupe author files job done. Folders={Folders} Scanned={Scanned} Groups={Groups} Deleted={Deleted} EmptyDeleted={Empty} BytesFreed={Bytes} DbRows={Rows}",
            foldersScanned, filesScanned, groups, deleted, emptyDeleted, bytesFreed, dbRows);
        _currentMessage = $"Done — {deleted} duplicate(s) removed across {foldersScanned} author folder(s), {emptyDeleted} empty file(s) deleted";
        return summary;
    }

    // The top-level author folder a file lives in: <root>/<firstSegment>. Returns
    // null for a file sitting directly under a root (no author folder).
    private static string? AuthorFolderDir(string fullPath, IReadOnlyList<string> roots)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return null;
        var root = roots.FirstOrDefault(r => fullPath.StartsWith(r, StringComparison.OrdinalIgnoreCase));
        if (root is null) return null;
        var rel = fullPath[root.Length..].TrimStart('\\', '/');
        var sep = rel.IndexOfAny(new[] { '\\', '/' });
        if (sep <= 0) return null;
        return Path.Combine(root, rel[..sep]);
    }

    private static async Task<HashSet<string>> LinkedPathsAmongAsync(
        LibraryDbContext db, IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in paths.Chunk(500))
        {
            var p = chunk.ToList();
            var linked = await db.LocalBookFiles.AsNoTracking()
                .Where(f => f.BookId != null && p.Contains(f.FullPath))
                .Select(f => f.FullPath)
                .ToListAsync(ct);
            foreach (var x in linked) set.Add(x);
        }
        return set;
    }

    private static async Task<int> PruneDbRowsAsync(
        LibraryDbContext db, IReadOnlyList<string> deletedPaths, CancellationToken ct)
    {
        if (deletedPaths.Count == 0) return 0;
        var removed = 0;
        foreach (var chunk in deletedPaths.Chunk(500))
        {
            ct.ThrowIfCancellationRequested();
            var paths = chunk.ToList();
            var lbf = await db.LocalBookFiles.Where(f => paths.Contains(f.FullPath)).ToListAsync(ct);
            var scans = await db.BookContentScans.Where(s => paths.Contains(s.FullPath)).ToListAsync(ct);
            db.LocalBookFiles.RemoveRange(lbf);
            db.BookContentScans.RemoveRange(scans);
            removed += lbf.Count + scans.Count;
        }
        if (removed > 0) await db.SaveChangesAsync(ct);
        return removed;
    }
}

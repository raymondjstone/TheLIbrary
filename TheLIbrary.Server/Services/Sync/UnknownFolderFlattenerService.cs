using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

public sealed record UnknownFolderFlattenSummary(
    int AuthorFoldersScanned,
    int FilesMoved,
    int DirectoriesRemoved,
    int DbRowsUpdated);

// Scheduled job: makes the __unknown quarantine FLAT. For every folder under a
// quarantine root it moves all contained files (recursively) up to the
// quarantine ROOT and then removes the now-empty folder tree — so the
// quarantine ends up as loose files with no author/title subfolders at all.
// LBF rows whose FullPath referenced a moved file are rewritten so the DB stays
// in sync (quarantine files usually have none).
//
// The quarantine is a flat bucket by policy: the reprocess-unknown job derives
// each file's author from its name/metadata, so folder grouping is worthless
// and only clutters the tree.
public sealed class UnknownFolderFlattenerService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly ILogger<UnknownFolderFlattenerService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;
    private UnknownFolderFlattenSummary? _lastResult;

    public UnknownFolderFlattenerService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        ILogger<UnknownFolderFlattenerService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;
    public UnknownFolderFlattenSummary? LastResult => _lastResult;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("flatten __unknown", out var holder))
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
            catch (Exception ex) { _log.LogError(ex, "Flatten __unknown job failed"); }
            finally { _isRunning = false; _currentMessage = null; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    private async Task<UnknownFolderFlattenSummary> RunAsync(CancellationToken ct)
    {
        _log.LogInformation("Flatten __unknown job starting");
        _currentMessage = "Scanning __unknown roots";
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

        var locations = await db.LibraryLocations
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);

        var unknownRoots = await UnknownFolderResolver.GetSourceRootsAsync(db, locations, ct);

        int authorFoldersScanned = 0, filesMoved = 0, dirsRemoved = 0, dbRowsUpdated = 0;
        var pathRewrites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var unknownRoot in unknownRoots)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(unknownRoot)) continue;

            foreach (var authorDir in Directory.GetDirectories(unknownRoot))
            {
                ct.ThrowIfCancellationRequested();
                authorFoldersScanned++;
                _currentMessage = $"Flattening folder {authorFoldersScanned}: {Path.GetFileName(authorDir)}";
                var (moved, removed) = FlattenFolderToRoot(unknownRoot, authorDir, pathRewrites);
                filesMoved += moved;
                dirsRemoved += removed;
            }
        }

        if (pathRewrites.Count > 0)
        {
            var affected = await db.LocalBookFiles
                .Where(f => pathRewrites.Keys.Contains(f.FullPath))
                .ToListAsync(ct);
            foreach (var row in affected)
            {
                if (pathRewrites.TryGetValue(row.FullPath, out var newPath))
                {
                    row.FullPath = newPath;
                    row.TitleFolder = Path.GetFileNameWithoutExtension(newPath);
                    dbRowsUpdated++;
                }
            }
            if (dbRowsUpdated > 0)
                await db.SaveChangesAsync(ct);
        }

        var summary = new UnknownFolderFlattenSummary(
            authorFoldersScanned, filesMoved, dirsRemoved, dbRowsUpdated);

        _log.LogInformation(
            "Flatten __unknown job done. AuthorFolders={Folders} Files={Files} DirsRemoved={Dirs} DbRows={Rows}",
            authorFoldersScanned, filesMoved, dirsRemoved, dbRowsUpdated);
        _currentMessage = $"Done — {filesMoved} file(s) moved, {dirsRemoved} dir(s) removed";

        return summary;
    }

    // Moves every file under `folder` (recursively) up to the quarantine ROOT,
    // then removes the now-empty folder tree (the folder itself included).
    // Records each (oldPath → newPath) rewrite so the DB pass can update matching
    // LBF rows. A file that fails to move leaves its directory non-empty, so that
    // directory is left in place (never recursively deleted) and retried next run.
    private (int filesMoved, int dirsRemoved) FlattenFolderToRoot(
        string unknownRoot,
        string folder,
        IDictionary<string, string> pathRewrites)
    {
        int filesMoved = 0, dirsRemoved = 0;

        foreach (var src in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList())
        {
            var dest = UniqueFilePath(Path.Combine(unknownRoot, Path.GetFileName(src)));
            try
            {
                File.Move(src, dest);
                pathRewrites[src] = dest;
                filesMoved++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Flatten: could not move {Src} → {Dest}", src, dest);
            }
        }

        // Remove emptied directories bottom-up, the folder itself last. Only ever
        // deletes a directory with no remaining entries — a file that didn't move
        // keeps its folder alive rather than being deleted with it.
        var dirs = Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories)
            .Append(folder)
            .OrderByDescending(p => p.Length)
            .ToList();
        foreach (var dir in dirs)
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                    dirsRemoved++;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Flatten: could not remove empty dir {Dir}", dir);
            }
        }

        return (filesMoved, dirsRemoved);
    }

    private static string UniqueFilePath(string preferred)
    {
        if (!File.Exists(preferred)) return preferred;
        var dir = Path.GetDirectoryName(preferred) ?? "";
        var stem = Path.GetFileNameWithoutExtension(preferred);
        var ext = Path.GetExtension(preferred);
        for (int n = 1; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem}_{n}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}

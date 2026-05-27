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

// Scheduled job: walks every author-level folder inside __unknown across all
// enabled library locations and moves any files nested in subdirectories up to
// the author folder root, then removes the now-empty subdirectories. LBF rows
// whose FullPath referenced a moved file are rewritten so the DB stays in sync.
//
// Off by default — the user opts in per environment because some setups
// intentionally use subfolders inside __unknown.
public sealed class UnknownFolderFlattenerService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly ILogger<UnknownFolderFlattenerService> _log;
    private volatile bool _isRunning;
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
            finally { _isRunning = false; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    private async Task<UnknownFolderFlattenSummary> RunAsync(CancellationToken ct)
    {
        _log.LogInformation("Flatten __unknown job starting");
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
                var (moved, removed) = FlattenAuthorFolder(authorDir, pathRewrites);
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

        return summary;
    }

    // Walks the author folder, moving every file in a subdirectory up to the
    // root, then deletes the now-empty subdirectories bottom-up. Records each
    // (oldPath → newPath) rewrite so the DB pass can update matching LBF rows.
    private (int filesMoved, int dirsRemoved) FlattenAuthorFolder(
        string authorDir,
        IDictionary<string, string> pathRewrites)
    {
        int filesMoved = 0, dirsRemoved = 0;

        var nestedFiles = Directory.EnumerateFiles(authorDir, "*", SearchOption.AllDirectories)
            .Where(p => !string.Equals(Path.GetDirectoryName(p), authorDir, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var src in nestedFiles)
        {
            var dest = UniqueFilePath(Path.Combine(authorDir, Path.GetFileName(src)));
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

        var subdirs = Directory.EnumerateDirectories(authorDir, "*", SearchOption.AllDirectories)
            .OrderByDescending(p => p.Length)
            .ToList();

        foreach (var dir in subdirs)
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
                _log.LogWarning(ex, "Flatten: could not remove empty subdir {Dir}", dir);
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

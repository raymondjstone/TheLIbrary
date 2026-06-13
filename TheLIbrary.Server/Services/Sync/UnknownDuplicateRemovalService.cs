using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

public sealed record UnknownDuplicateRemovalSummary(
    int FilesScanned,
    int FilesHashed,
    int HashFailures,
    int DuplicateGroups,
    int FilesDeleted,
    int EmptyFilesDeleted,
    long BytesFreed,
    int DbRowsRemoved);

// Scheduled job: finds byte-identical duplicate files anywhere inside the
// __unknown quarantine (across all enabled library locations, or the custom
// quarantine path) and deletes all but one copy of each.
//
// "Duplicate" means identical contents, nothing less: candidates are grouped
// by file size first (no file reads — cheap even on the NAS mount), and only
// same-size groups get read and SHA-256-hashed. Different names with the same
// bytes are duplicates; the same name with different bytes is not — both stay.
//
// The kept copy is the one with the shortest full path (ties broken
// alphabetically) so un-suffixed originals win over "_1"-style collision
// copies. Zero-byte files are deleted outright — an empty ebook is junk, not
// a duplicate. DB rows pointing at a deleted path (UnknownFiles index,
// UnknownFileChecks, LocalBookFiles, BookContentScans) are removed so no
// stale row survives the cleanup.
//
// Off by default — deleting files is destructive, so the user opts in.
public sealed class UnknownDuplicateRemovalService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly ILogger<UnknownDuplicateRemovalService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;
    private UnknownDuplicateRemovalSummary? _lastResult;

    public UnknownDuplicateRemovalService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        ILogger<UnknownDuplicateRemovalService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;
    public UnknownDuplicateRemovalSummary? LastResult => _lastResult;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("dedupe __unknown", out var holder))
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
                _log.LogError(ex, "Dedupe __unknown job failed");
                // Deleting files is the one job where "went quiet" must never
                // be the failure mode — keep the error visible in the status.
                _currentMessage = $"Failed: {ex.Message}";
            }
            finally { _isRunning = false; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    private async Task<UnknownDuplicateRemovalSummary> RunAsync(CancellationToken ct)
    {
        _log.LogInformation("Dedupe __unknown job starting");
        _currentMessage = "Scanning __unknown roots";
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

        var locations = await db.LibraryLocations
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);

        var unknownRoots = await UnknownFolderResolver.GetSourceRootsAsync(db, locations, ct);

        // Candidate set: every file under every quarantine root. The shared
        // scanner does the size-group → SHA-256 → keeper work (the single
        // definition of "byte-identical duplicate"). IgnoreInaccessible so one
        // unreadable subfolder can't abort a 50k-file walk on the NAS mount.
        var walkOpts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        var candidates = unknownRoots
            .Where(Directory.Exists)
            .SelectMany(r => Directory.EnumerateFiles(r, "*", walkOpts));

        var scan = await ContentDuplicateScanner.ScanAndDeleteAsync(
            candidates, _log, msg => _currentMessage = msg, ct);
        var deletedPaths = scan.DeletedPaths;

        // Drop DB rows that pointed at deleted files. Chunked so the IN clause
        // stays a sane size when a run removes thousands of copies.
        int dbRowsRemoved = 0;
        foreach (var chunk in deletedPaths.Chunk(500))
        {
            ct.ThrowIfCancellationRequested();
            var paths = chunk.ToList();
            var unknownRows = await db.UnknownFiles.Where(f => paths.Contains(f.FullPath)).ToListAsync(ct);
            var checkRows = await db.UnknownFileChecks.Where(f => paths.Contains(f.FullPath)).ToListAsync(ct);
            var lbfRows = await db.LocalBookFiles.Where(f => paths.Contains(f.FullPath)).ToListAsync(ct);
            var scanRows = await db.BookContentScans.Where(s => paths.Contains(s.FullPath)).ToListAsync(ct);
            db.UnknownFiles.RemoveRange(unknownRows);
            db.UnknownFileChecks.RemoveRange(checkRows);
            db.LocalBookFiles.RemoveRange(lbfRows);
            db.BookContentScans.RemoveRange(scanRows);
            dbRowsRemoved += unknownRows.Count + checkRows.Count + lbfRows.Count + scanRows.Count;
        }
        if (dbRowsRemoved > 0)
            await db.SaveChangesAsync(ct);

        var summary = new UnknownDuplicateRemovalSummary(
            scan.FilesScanned, scan.FilesHashed, scan.HashFailures, scan.DuplicateGroups,
            scan.FilesDeleted, scan.EmptyFilesDeleted, scan.BytesFreed, dbRowsRemoved);

        _log.LogInformation(
            "Dedupe __unknown job done. Scanned={Scanned} Hashed={Hashed} HashFailures={Failures} Groups={Groups} Deleted={Deleted} EmptyDeleted={Empty} BytesFreed={Bytes} DbRows={Rows} NearDuplicates={Lookalikes}",
            scan.FilesScanned, scan.FilesHashed, scan.HashFailures, scan.DuplicateGroups,
            scan.FilesDeleted, scan.EmptyFilesDeleted, scan.BytesFreed, dbRowsRemoved, scan.NearDuplicates);
        _currentMessage = $"Done — {scan.FilesDeleted} duplicate(s) removed in {scan.DuplicateGroups} group(s), {scan.EmptyFilesDeleted} empty file(s) deleted"
            + (scan.HashFailures > 0 ? $", {scan.HashFailures} file(s) unreadable" : "");

        return summary;
    }
}

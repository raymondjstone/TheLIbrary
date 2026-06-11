using System.Security.Cryptography;
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

        // Pass 1: directory walk only — collect every file's size. Files with a
        // unique size can't have a byte-identical twin, so they're never read.
        // Zero-byte files are unambiguous junk (failed downloads with no content
        // at all) and are deleted outright.
        var bySize = new Dictionary<long, List<string>>();
        var deletedPaths = new List<string>();
        int filesScanned = 0, emptyDeleted = 0;
        foreach (var unknownRoot in unknownRoots)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(unknownRoot)) continue;

            // IgnoreInaccessible: one unreadable subfolder must not abort the
            // walk of a 50k+ file quarantine on the NAS mount.
            var walkOpts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (var path in Directory.EnumerateFiles(unknownRoot, "*", walkOpts))
            {
                ct.ThrowIfCancellationRequested();
                filesScanned++;
                if (filesScanned % 1000 == 0)
                    _currentMessage = $"Scanned {filesScanned} file(s)";
                long size;
                try { size = new FileInfo(path).Length; }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Dedupe: could not stat {Path}", path);
                    continue;
                }
                if (size == 0)
                {
                    try
                    {
                        File.Delete(path);
                        deletedPaths.Add(path);
                        emptyDeleted++;
                        _log.LogInformation("Dedupe: deleted zero-byte file {Path}", path);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Dedupe: could not delete zero-byte file {Path}", path);
                    }
                    continue;
                }
                if (!bySize.TryGetValue(size, out var list)) bySize[size] = list = new List<string>();
                list.Add(path);
            }
        }

        // Pass 2: hash only the same-size groups and split them by content.
        int filesHashed = 0, hashFailures = 0, duplicateGroups = 0, filesDeleted = 0;
        int lookalikes = 0, lookalikesLogged = 0;
        long bytesFreed = 0;
        foreach (var (size, paths) in bySize.Where(kv => kv.Value.Count > 1))
        {
            ct.ThrowIfCancellationRequested();

            var byHash = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                filesHashed++;
                _currentMessage = $"Hashing candidate {filesHashed}: {Path.GetFileName(path)}";
                string hash;
                try
                {
                    await using var stream = File.OpenRead(path);
                    hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
                }
                catch (Exception ex)
                {
                    hashFailures++;
                    _log.LogWarning(ex, "Dedupe: could not hash {Path}", path);
                    continue;
                }
                if (!byHash.TryGetValue(hash, out var list)) byHash[hash] = list = new List<string>();
                list.Add(path);
            }

            // Diagnostic: same filename + same size but different bytes is the
            // classic "looks like a duplicate but isn't one" case (re-downloads
            // of the same book repackage the zip, so the bytes differ). Surface
            // a sample in the log so a zero-deletion run over a quarantine full
            // of NEAR-duplicates is explainable without guesswork.
            if (byHash.Count > 1)
            {
                var sameNameDifferentBytes = byHash
                    .SelectMany(kv => kv.Value, (kv, path) => (Hash: kv.Key, Path: path))
                    .GroupBy(x => Path.GetFileName(x.Path), StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Select(x => x.Hash).Distinct(StringComparer.Ordinal).Count() > 1);
                foreach (var g in sameNameDifferentBytes)
                {
                    lookalikes++;
                    if (lookalikesLogged < 20)
                    {
                        lookalikesLogged++;
                        _log.LogInformation(
                            "Dedupe: same name and size but different contents — NOT byte-identical, kept: {Paths}",
                            string.Join(" | ", g.Select(x => x.Path)));
                    }
                }
            }

            foreach (var group in byHash.Values.Where(g => g.Count > 1))
            {
                duplicateGroups++;
                var keep = ChooseKeeper(group);
                foreach (var dup in group.Where(p => !string.Equals(p, keep, StringComparison.OrdinalIgnoreCase)))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        File.Delete(dup);
                        deletedPaths.Add(dup);
                        filesDeleted++;
                        bytesFreed += size;
                        // Information, not Debug — deletions must be auditable
                        // in the default container log.
                        _log.LogInformation("Dedupe: deleted {Dup} (identical to {Keep})", dup, keep);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Dedupe: could not delete {Path}", dup);
                    }
                }
            }
        }

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
            filesScanned, filesHashed, hashFailures, duplicateGroups, filesDeleted, emptyDeleted, bytesFreed, dbRowsRemoved);

        _log.LogInformation(
            "Dedupe __unknown job done. Scanned={Scanned} Hashed={Hashed} HashFailures={Failures} Groups={Groups} Deleted={Deleted} EmptyDeleted={Empty} BytesFreed={Bytes} DbRows={Rows} NearDuplicates={Lookalikes}",
            filesScanned, filesHashed, hashFailures, duplicateGroups, filesDeleted, emptyDeleted, bytesFreed, dbRowsRemoved, lookalikes);
        _currentMessage = $"Done — {filesDeleted} duplicate(s) removed in {duplicateGroups} group(s), {emptyDeleted} empty file(s) deleted"
            + (hashFailures > 0 ? $", {hashFailures} file(s) unreadable" : "");

        return summary;
    }

    // The copy that survives: shortest full path first (un-suffixed originals
    // and shallower locations win), alphabetical as the tiebreak so the
    // outcome is deterministic.
    internal static string ChooseKeeper(IReadOnlyList<string> group)
        => group
            .OrderBy(p => p.Length)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .First();
}

using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.IO;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

public sealed record LinkedAuthorMergeSummary(int Merged, int FilesMoved, int Failed);

// Scheduled job: every author the user has LINKED to a canonical (non-pen-name
// LinkedToAuthorId) is fully merged into that canonical — books reassigned
// (deduped by OpenLibrary key, a duplicate's files re-homed onto the kept copy),
// every file physically RELOCATED on disk into the canonical's author folder,
// series re-pointed, then the duplicate author deleted.
//
// Moving the files on disk is what makes the merge durable: author↔file
// association is driven by the on-disk folder, so a DB-only reassignment is just
// reverted by the next sync (which re-derives the author from the folder and even
// re-creates deleted author records). Only acts on explicit user links, so it
// never guesses that two same-name authors are the same person; pen-name links
// are left alone. Chains (A->B->C) resolve to the ultimate root canonical.
public sealed class LinkedAuthorMergeService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly IFileSystem _fs;
    private readonly ILogger<LinkedAuthorMergeService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;
    private LinkedAuthorMergeSummary? _lastResult;

    public LinkedAuthorMergeService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        IFileSystem fs,
        ILogger<LinkedAuthorMergeService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _fs = fs;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;
    public LinkedAuthorMergeSummary? LastResult => _lastResult;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("merge-linked-authors", out var holder))
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
            catch (Exception ex) { _log.LogError(ex, "Linked-author merge job failed"); }
            finally { _isRunning = false; _currentMessage = null; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    private async Task<LinkedAuthorMergeSummary> RunAsync(CancellationToken ct)
    {
        _currentMessage = "Loading author links";
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

        var links = await db.Authors.AsNoTracking()
            .Where(a => a.LinkedToAuthorId != null && !a.IsPenName)
            .Select(a => new { a.Id, Target = a.LinkedToAuthorId!.Value })
            .ToListAsync(ct);
        if (links.Count == 0)
        {
            _currentMessage = "Done — no linked authors to merge";
            return new LinkedAuthorMergeSummary(0, 0, 0);
        }

        var linkTo = links.ToDictionary(a => a.Id, a => a.Target);
        int Root(int id)
        {
            var seen = new HashSet<int>();
            var cur = id;
            while (seen.Add(cur) && linkTo.TryGetValue(cur, out var next)) cur = next;
            return cur;
        }

        var locations = await db.LibraryLocations.AsNoTracking()
            .Where(l => l.Enabled).Select(l => l.Path).ToListAsync(ct);

        int merged = 0, filesMoved = 0, failed = 0;
        for (var i = 0; i < links.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var sourceId = links[i].Id;
            var targetId = Root(sourceId);
            if (targetId == sourceId) continue;

            _currentMessage = $"Merging {i + 1}/{links.Count} (author {sourceId} → {targetId})";
            try
            {
                filesMoved += await MergeOneAsync(db, sourceId, targetId, locations, ct);
                merged++;
            }
            catch (Exception ex)
            {
                failed++;
                _log.LogWarning(ex, "Linked-author merge: failed to merge {Source} into {Target}", sourceId, targetId);
            }
        }

        _log.LogInformation(
            "Linked-author merge done — merged {Merged}, files moved {Files}, failed {Failed}",
            merged, filesMoved, failed);
        _currentMessage = $"Done — merged {merged}, files moved {filesMoved}, failed {failed}";
        return new LinkedAuthorMergeSummary(merged, filesMoved, failed);
    }

    private async Task<int> MergeOneAsync(
        LibraryDbContext db, int sourceId, int targetId, IReadOnlyList<string> locations, CancellationToken ct)
    {
        // EnableRetryOnFailure is configured, so a user-initiated transaction must
        // run through the execution strategy (as one retriable unit).
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
        db.ChangeTracker.Clear(); // start each (re)try from a clean slate
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var source = await db.Authors.AsNoTracking().FirstOrDefaultAsync(a => a.Id == sourceId, ct);
        var target = await db.Authors.AsNoTracking().FirstOrDefaultAsync(a => a.Id == targetId, ct);
        if (source is null || target is null) { await tx.RollbackAsync(ct); return 0; }

        var canonicalFolder = SanitizeFolder(target.CalibreFolderName ?? target.Name ?? "");
        var childFolders = FolderCandidates(source);

        // --- Dedupe books by OL key (re-home a duplicate's files, drop the dup). ---
        var byKey = await db.Books.AsNoTracking()
            .Where(b => b.AuthorId == targetId && b.OpenLibraryWorkKey != null && b.OpenLibraryWorkKey != "")
            .Select(b => new { Key = b.OpenLibraryWorkKey!, b.Id })
            .ToDictionaryAsync(x => x.Key, x => x.Id, StringComparer.Ordinal, ct);
        var sourceBooks = await db.Books.AsNoTracking()
            .Where(b => b.AuthorId == sourceId)
            .Select(b => new { b.Id, Key = b.OpenLibraryWorkKey })
            .ToListAsync(ct);

        var dupToKeep = new List<(int Dup, int Keep)>();
        foreach (var b in sourceBooks)
        {
            if (string.IsNullOrEmpty(b.Key)) continue;
            if (byKey.TryGetValue(b.Key, out var keepId)) dupToKeep.Add((b.Id, keepId));
            else byKey[b.Key] = b.Id;
        }
        foreach (var (dup, keep) in dupToKeep)
            await db.LocalBookFiles.Where(f => f.BookId == dup)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.BookId, keep), ct);
        if (dupToKeep.Count > 0)
        {
            var dupIds = dupToKeep.Select(d => d.Dup).ToList();
            await db.Books.Where(b => dupIds.Contains(b.Id)).ExecuteDeleteAsync(ct);
        }
        await db.Books.Where(b => b.AuthorId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.AuthorId, targetId), ct);

        // --- Relocate the source's files on disk into the canonical's folder,
        //     so the move survives the next sync (which keys off the folder). ---
        // Archived files are inert (see ArchivePolicy) — leave any archived copy
        // of the source author where it is instead of dragging it into the
        // canonical folder (which would resurrect it as a live duplicate).
        var archiveLeaf = await ArchivePolicy.LoadLeafAsync(db, ct);
        var files = await db.LocalBookFiles.Where(f => f.AuthorId == sourceId)
            .Where(ArchivePolicy.NotUnder(archiveLeaf))
            .ToListAsync(ct);
        var moved = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            file.AuthorId = targetId;
            if (!string.IsNullOrWhiteSpace(canonicalFolder)) file.AuthorFolder = canonicalFolder;
            if (string.IsNullOrWhiteSpace(file.FullPath)) continue;

            var loc = locations.FirstOrDefault(l =>
                file.FullPath.StartsWith(l.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
            if (loc is null) continue;
            var libRoot = loc.TrimEnd('\\', '/');
            var relative = file.FullPath[libRoot.Length..].TrimStart('\\', '/');
            var firstSep = relative.IndexOfAny(new[] { '\\', '/' });
            if (firstSep < 0) continue;
            var existingFolder = relative[..firstSep];
            if (!childFolders.Contains(existingFolder, StringComparer.OrdinalIgnoreCase)) continue;

            var remainder = relative[(firstSep + 1)..];
            var destPath = Path.Combine(libRoot, canonicalFolder, remainder);
            var destDir = Path.GetDirectoryName(destPath);
            try
            {
                if (destDir is not null) await _fs.CreateDirectoryAsync(destDir, ct);
                if (FsPath.SameLocation(file.FullPath, destPath)) { /* already there */ }
                else if (await _fs.FileExistsAsync(file.FullPath, ct))
                {
                    var final = await UniqueFileAsync(destPath, ct);
                    await _fs.MoveFileAsync(file.FullPath, final, overwrite: false, ct);
                    file.FullPath = final; moved++;
                }
                else if (await _fs.DirectoryExistsAsync(file.FullPath, ct))
                {
                    var final = await UniqueDirAsync(Path.GetDirectoryName(destPath)!, Path.GetFileName(destPath), ct);
                    await _fs.MoveDirectoryAsync(file.FullPath, final, ct);
                    file.FullPath = final; moved++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Linked-author merge: could not move file #{Id}", file.Id);
            }
        }
        await db.SaveChangesAsync(ct);

        await db.Series.Where(s => s.PrimaryAuthorId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.PrimaryAuthorId, targetId), ct);
        await db.Authors.Where(a => a.LinkedToAuthorId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.LinkedToAuthorId, (int?)null), ct);
        await db.Authors.Where(a => a.Id == sourceId).ExecuteDeleteAsync(ct);

        await tx.CommitAsync(ct);
        return moved;
        });
    }

    private static List<string> FolderCandidates(Author a)
    {
        var list = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(a.Name)) list.Add(a.Name);
        if (!string.IsNullOrWhiteSpace(a.CalibreFolderName)
            && !list.Contains(a.CalibreFolderName, StringComparer.OrdinalIgnoreCase))
            list.Add(a.CalibreFolderName);
        return list;
    }

    private static string SanitizeFolder(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        var result = sb.ToString().TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(result) ? "_" : result;
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

    private async Task<string> UniqueDirAsync(string parent, string leaf, CancellationToken ct)
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
}

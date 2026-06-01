using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.IO;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

public sealed record ForeignArchiveSummary(int Archived, int Skipped, int Errors);

// Scheduled job: every local file that belongs to a title the user has
// CONFIRMED as foreign (Book.LanguageReview == ConfirmedForeign) is moved into
// the same archive folder the dedupe "Archive extras" action uses
// (AppSettings["DedupeArchiveFolder"], default "__archive"). The book's
// library-relative subfolders are preserved underneath, so the move mirrors the
// dedupe behaviour and the files show up on the Archived Files page.
//
// Idempotent: files already living under the archive folder are skipped, so the
// daily run is a no-op once everything confirmed-foreign has been swept.
public sealed class ForeignArchiveService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly IFileSystem _fs;
    private readonly ILogger<ForeignArchiveService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;
    private ForeignArchiveSummary? _lastResult;

    public ForeignArchiveService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        IFileSystem fs,
        ILogger<ForeignArchiveService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _fs = fs;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;
    public ForeignArchiveSummary? LastResult => _lastResult;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("archive confirmed-foreign titles", out var holder))
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
            catch (Exception ex) { _log.LogError(ex, "Foreign-archive job failed"); }
            finally { _isRunning = false; _currentMessage = null; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    private async Task<ForeignArchiveSummary> RunAsync(CancellationToken ct)
    {
        _log.LogInformation("Foreign-archive job starting");
        _currentMessage = "Loading confirmed-foreign titles";
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

        var locations = await db.LibraryLocations.AsNoTracking()
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);

        var storedLeaf = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.DedupeArchiveFolder)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        // Normalise to forward-slash (the library is on a Linux mount and stored
        // paths are always '/'). A leaf name has no separator; a full path does.
        var archiveLeaf = (string.IsNullOrWhiteSpace(storedLeaf) ? "__archive" : storedLeaf.Trim())
            .Replace('\\', '/').TrimEnd('/');
        var archiveIsAbsolute = archiveLeaf.Contains('/');

        // Files linked to a title the user explicitly confirmed foreign.
        var files = await db.LocalBookFiles
            .Where(f => f.BookId != null
                && f.Book!.LanguageReview == LanguageReview.ConfirmedForeign)
            .ToListAsync(ct);

        if (files.Count == 0)
        {
            _log.LogInformation("Foreign-archive job found no confirmed-foreign files");
            _currentMessage = "Done — nothing to archive";
            return new ForeignArchiveSummary(0, 0, 0);
        }

        var archived = 0;
        var skipped = 0;
        var errors = 0;

        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];
            _currentMessage = $"Archiving {i + 1}/{files.Count}";

            if (string.IsNullOrWhiteSpace(file.FullPath))
            {
                skipped++;
                continue;
            }

            // Already under the archive folder — nothing to do (keeps the daily
            // run idempotent).
            if (IsAlreadyArchived(file.FullPath, archiveLeaf, archiveIsAbsolute))
            {
                skipped++;
                continue;
            }

            var location = locations.FirstOrDefault(l =>
                file.FullPath.StartsWith(l.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
            if (location is null)
            {
                _log.LogWarning("Foreign-archive: file #{Id} is outside enabled library roots: {Path}",
                    file.Id, file.FullPath);
                skipped++;
                continue;
            }

            var libRoot = location.TrimEnd('\\', '/');
            var relative = file.FullPath[libRoot.Length..].TrimStart('\\', '/');
            var destBase = archiveIsAbsolute
                ? archiveLeaf
                : Path.Combine(libRoot, archiveLeaf);
            var destPath = Path.Combine(destBase, relative);
            var destDir = Path.GetDirectoryName(destPath);

            try
            {
                if (destDir is not null) await _fs.CreateDirectoryAsync(destDir, ct);
                if (await _fs.FileExistsAsync(file.FullPath, ct))
                {
                    var final = await UniqueFileAsync(destPath, ct);
                    await _fs.MoveFileAsync(file.FullPath, final, overwrite: false, ct);
                    file.FullPath = final;
                    archived++;
                }
                else if (await _fs.DirectoryExistsAsync(file.FullPath, ct))
                {
                    var final = await UniqueDirectoryAsync(
                        Path.GetDirectoryName(destPath)!, Path.GetFileName(destPath), ct);
                    await _fs.MoveDirectoryAsync(file.FullPath, final, ct);
                    file.FullPath = final;
                    archived++;
                }
                else
                {
                    _log.LogWarning("Foreign-archive: file #{Id} no longer exists on disk: {Path}",
                        file.Id, file.FullPath);
                    skipped++;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Foreign-archive: failed to move file #{Id}", file.Id);
                errors++;
            }

            // Persist incrementally so a long run that gets cancelled keeps the
            // paths it already moved in sync with disk.
            if ((i + 1) % 50 == 0) await db.SaveChangesAsync(ct);
        }

        await db.SaveChangesAsync(ct);

        _log.LogInformation(
            "Foreign-archive job done — archived {Archived}, skipped {Skipped}, errors {Errors}",
            archived, skipped, errors);
        _currentMessage = $"Done — archived {archived}, skipped {skipped}, errors {errors}";
        return new ForeignArchiveSummary(archived, skipped, errors);
    }

    // True when the path already sits under the archive folder, whether that
    // folder is a simple leaf name (matched as a path component) or a full
    // absolute path (matched as a prefix). Forward-slash based to match the
    // stored paths regardless of host OS. Mirrors ArchivedFilesController.
    private static bool IsAlreadyArchived(string fullPath, string archiveLeaf, bool archiveIsAbsolute)
    {
        if (archiveIsAbsolute)
            return fullPath.StartsWith(archiveLeaf + "/", StringComparison.OrdinalIgnoreCase);
        return fullPath.Contains("/" + archiveLeaf + "/", StringComparison.OrdinalIgnoreCase);
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
}

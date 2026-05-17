using Microsoft.EntityFrameworkCore;
using SharpCompress.Archives;
using SharpCompress.Common;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

// Scans all LocalBookFile records for .zip and .rar files, extracts their
// contents to the configured incoming folder, then deletes the archive from
// disk and removes the DB record. Starred authors are processed first.
//
// Runs as a singleton through BackgroundTaskCoordinator — cannot overlap with
// sync, incoming, or any other background job.
public sealed class UnzipService
{

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly ILogger<UnzipService> _log;
    private volatile bool _isRunning;

    public UnzipService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        ILogger<UnzipService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _log = log;
    }

    public bool IsRunning => _isRunning;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("unzip", out var holder))
        {
            error = $"Another task is already running ({holder})";
            return false;
        }
        error = null;
        _isRunning = true;
        _ = Task.Run(async () =>
        {
            try { await RunAsync(hostCt); }
            catch (OperationCanceledException) when (hostCt.IsCancellationRequested) { }
            catch (Exception ex) { _log.LogError(ex, "Unzip job failed"); }
            finally { _isRunning = false; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        _log.LogInformation("Unzip job starting");
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

        var incomingSetting = await db.AppSettings
            .FirstOrDefaultAsync(s => s.Key == AppSettingKeys.IncomingFolder, ct);
        var incomingPath = incomingSetting?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(incomingPath))
        {
            _log.LogWarning("Unzip job: incoming folder not configured — aborting");
            return;
        }
        if (!Directory.Exists(incomingPath))
        {
            _log.LogWarning("Unzip job: incoming folder does not exist: {Path}", incomingPath);
            return;
        }

        // Load author priorities for ordering (projection avoids touching
        // columns that may not yet exist if a migration is pending).
        var priorities = await db.Authors
            .Select(a => new { a.Id, a.Priority })
            .ToDictionaryAsync(a => a.Id, a => a.Priority, ct);

        var files = await db.LocalBookFiles.ToListAsync(ct);

        files.Sort((x, y) =>
        {
            var px = x.AuthorId.HasValue && priorities.TryGetValue(x.AuthorId.Value, out var xp) ? xp : 0;
            var py = y.AuthorId.HasValue && priorities.TryGetValue(y.AuthorId.Value, out var yp) ? yp : 0;
            if (py != px) return py.CompareTo(px);
            var af = string.Compare(x.AuthorFolder, y.AuthorFolder, StringComparison.OrdinalIgnoreCase);
            if (af != 0) return af;
            return string.Compare(x.FullPath, y.FullPath, StringComparison.OrdinalIgnoreCase);
        });

        _log.LogInformation("Unzip job: {Count} record(s) to scan, incoming: {Incoming}",
            files.Count, incomingPath);

        int extracted = 0, skipped = 0, errors = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            // Resolve the accessible path (strip \\server\share UNC prefix if needed)
            var effectivePath = ResolveEffectivePath(file.FullPath);

            if (!CalibreScanner.ArchiveExtensions.Contains(Path.GetExtension(effectivePath)))
            {
                skipped++;
                continue;
            }

            if (!File.Exists(effectivePath))
            {
                _log.LogWarning("Unzip: archive not found on disk, removing DB record [{Id}]: {Path}",
                    file.Id, effectivePath);
                db.LocalBookFiles.Remove(file);
                await db.SaveChangesAsync(ct);
                continue;
            }

            _log.LogInformation("Unzip [{Id}]: {Path} -> {Incoming}", file.Id, effectivePath, incomingPath);

            try
            {
                ExtractArchive(effectivePath, incomingPath);

                File.Delete(effectivePath);
                db.LocalBookFiles.Remove(file);
                await db.SaveChangesAsync(ct);

                extracted++;
                _log.LogInformation("Unzip [{Id}]: done", file.Id);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Unzip [{Id}]: failed to extract {Path}", file.Id, effectivePath);
                errors++;
            }
        }

        _log.LogInformation(
            "Unzip job done. Extracted={Extracted} Skipped={Skipped} Errors={Errors}",
            extracted, skipped, errors);
    }

    private static void ExtractArchive(string archivePath, string destinationDir)
    {
        using var archive = ArchiveFactory.Open(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory) continue;

            // Flatten: strip any path inside the archive and drop straight into
            // the incoming folder so IncomingProcessor can find the files.
            var fileName = Path.GetFileName(entry.Key ?? entry.ToString()!);
            if (string.IsNullOrWhiteSpace(fileName)) continue;

            var destPath = UniqueDestinationPath(destinationDir, fileName);
            entry.WriteToFile(destPath, new ExtractionOptions
            {
                ExtractFullPath = false,
                Overwrite = false,
            });
        }
    }

    private static string UniqueDestinationPath(string dir, string fileName)
    {
        var dest = Path.Combine(dir, fileName);
        if (!File.Exists(dest)) return dest;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext  = Path.GetExtension(fileName);
        for (int n = 1; ; n++)
        {
            dest = Path.Combine(dir, $"{stem}_{n}{ext}");
            if (!File.Exists(dest)) return dest;
        }
    }

    // Strips \\server\share from a UNC path so container-local mounts are used.
    private static string ResolveEffectivePath(string path)
    {
        var n = path.Replace('\\', '/');
        if (!n.StartsWith("//")) return n;
        int i1 = n.IndexOf('/', 2);
        if (i1 < 0) return n;
        int i2 = n.IndexOf('/', i1 + 1);
        if (i2 < 0) return n;
        return n[i2..];
    }
}

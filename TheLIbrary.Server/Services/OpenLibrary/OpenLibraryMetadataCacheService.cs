using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.IO;
using TheLibrary.Server.Services.Scheduling;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Services.OpenLibrary;

public sealed record OpenLibraryMetadataCacheSummary(int BooksUpdated, int CoversCached);

public sealed class OpenLibraryMetadataCacheService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly OpenLibraryClient _ol;
    private readonly IFileSystem _fs;
    private readonly CoverCacheState _coverCache;
    private readonly ILogger<OpenLibraryMetadataCacheService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;
    private OpenLibraryMetadataCacheSummary? _lastResult;

    // Default books processed per run (overridable via the Settings page). The
    // candidate set is scoped to owned books and every processed book is marked
    // done, so the backlog clears monotonically.
    public const int DefaultBatchSize = 1000;

    public OpenLibraryMetadataCacheService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        OpenLibraryClient ol,
        IFileSystem fs,
        CoverCacheState coverCache,
        ILogger<OpenLibraryMetadataCacheService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _ol = ol;
        _fs = fs;
        _coverCache = coverCache;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;
    public OpenLibraryMetadataCacheSummary? LastResult => _lastResult;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("cache-openlibrary-metadata", out var holder))
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
            catch (Exception ex) { _log.LogError(ex, "OpenLibrary metadata cache failed"); }
            finally { _isRunning = false; _currentMessage = null; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    private async Task<OpenLibraryMetadataCacheSummary> RunAsync(CancellationToken ct)
    {
        _currentMessage = "Loading books to cache";
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

        var batchRaw = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.CacheMetadataBatchSize)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);
        var batchSize = int.TryParse(batchRaw, out var n) && n > 0
            ? Math.Clamp(n, 1, 100_000) : DefaultBatchSize;

        // Only cache metadata for books the user actually OWNS (an ebook file or
        // a manually-owned physical copy). The full OL-keyed catalogue is ~1.6M
        // rows — mostly unowned "missing works" whose covers the UI already shows
        // by hot-linking OpenLibrary — so caching those locally is pointless.
        var candidates = await db.Books
            .Where(b => !string.IsNullOrWhiteSpace(b.OpenLibraryWorkKey)
                     && !b.OpenLibraryWorkKey.StartsWith(ManualWorkKey.Prefix)
                     && (b.Subjects == null || b.CoverUrl == null)
                     && (b.ManuallyOwned || b.OwnedDifferentEdition || b.LocalFiles.Any()))
            .OrderByDescending(b => b.Author.Priority)
            .ThenBy(b => b.Author.Name)
            .ThenBy(b => b.Title)
            .Take(batchSize)
            .ToListAsync(ct);

        var booksUpdated = 0;
        var coversCached = 0;

        var cacheRoot = _coverCache.Directory;
        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            _log.LogWarning("No cover-cache directory configured — skipping run.");
            return new OpenLibraryMetadataCacheSummary(0, 0);
        }
        try
        {
            await _fs.CreateDirectoryAsync(cacheRoot, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // e.g. the default /app/wwwroot is read-only in a container. Don't
            // crash or hammer OpenLibrary — tell the user to set a writable path.
            _log.LogWarning(ex,
                "Cover-cache directory '{Dir}' is not writable — set a writable path on the Settings page. Skipping run.",
                cacheRoot);
            return new OpenLibraryMetadataCacheSummary(0, 0);
        }

        foreach (var book in candidates)
        {
            ct.ThrowIfCancellationRequested();
            _currentMessage = $"Caching metadata {booksUpdated + 1}/{candidates.Count}: {book.Title}";
            try
            {
                var work = await _ol.FetchWorkAsync(book.OpenLibraryWorkKey, ct);
                if (work is null) continue;

                if (book.Subjects == null)
                {
                    book.Subjects = work.Subjects is { Count: > 0 }
                        ? string.Join(';', work.Subjects.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()))
                        : "";
                    booksUpdated++;
                }

                if (book.CoverUrl == null)
                {
                    string? cachedRel = null;
                    if (work.Covers is { Count: > 0 })
                    {
                        var coverId = work.Covers.First();
                        var rel = $"/cached-covers/{coverId}.jpg";
                        var disk = Path.Combine(cacheRoot, $"{coverId}.jpg");
                        if (!await _fs.FileExistsAsync(disk, ct))
                        {
                            var bytes = await _ol.DownloadCoverBytesAsync(coverId, ct);
                            if (bytes is not null)
                            {
                                await File.WriteAllBytesAsync(disk, bytes, ct);
                                coversCached++;
                            }
                        }
                        if (await _fs.FileExistsAsync(disk, ct))
                            cachedRel = rel;
                    }

                    // Always record an outcome: the cached path, or "" meaning
                    // "tried, no cover to cache" — so this book leaves the
                    // candidate set instead of being re-fetched every run. An
                    // empty CoverUrl is falsy in the UI, which falls back to the
                    // OpenLibrary cover id, so display is unaffected.
                    book.CoverUrl = cachedRel ?? "";
                    booksUpdated++;
                }
            }
            catch (OpenLibraryRequestFailedException ex)
            {
                _log.LogWarning(ex, "Skipping metadata cache refresh for work {WorkKey}", book.OpenLibraryWorkKey);
            }
        }

        await db.SaveChangesAsync(ct);
        _currentMessage = $"Done — {booksUpdated} book(s) updated, {coversCached} cover(s) cached";
        return new OpenLibraryMetadataCacheSummary(booksUpdated, coversCached);
    }
}

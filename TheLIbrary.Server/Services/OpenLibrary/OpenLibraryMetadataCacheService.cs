using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
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
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<OpenLibraryMetadataCacheService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;
    private OpenLibraryMetadataCacheSummary? _lastResult;

    public OpenLibraryMetadataCacheService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        OpenLibraryClient ol,
        IFileSystem fs,
        IWebHostEnvironment env,
        ILogger<OpenLibraryMetadataCacheService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _ol = ol;
        _fs = fs;
        _env = env;
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

        var candidates = await db.Books
            .Where(b => !string.IsNullOrWhiteSpace(b.OpenLibraryWorkKey)
                     && !b.OpenLibraryWorkKey.StartsWith(ManualWorkKey.Prefix)
                     && (b.Subjects == null || b.CoverUrl == null))
            .OrderByDescending(b => b.Author.Priority)
            .ThenBy(b => b.Author.Name)
            .ThenBy(b => b.Title)
            .Take(100)
            .ToListAsync(ct);

        var booksUpdated = 0;
        var coversCached = 0;
        var cacheRoot = Path.Combine(_env.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot"), "cached-covers");
        await _fs.CreateDirectoryAsync(cacheRoot, ct);

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

                if (book.CoverUrl == null && work.Covers is { Count: > 0 })
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
                    {
                        book.CoverUrl = rel;
                        booksUpdated++;
                    }
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

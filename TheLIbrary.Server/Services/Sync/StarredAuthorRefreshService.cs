using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

// On-demand job that immediately refreshes works for every starred author
// (Priority >= 1). Not scheduled — triggered only via POST /api/jobs/refresh-starred/start.
// Runs through BackgroundTaskCoordinator so it cannot overlap with sync or
// other exclusive jobs.
public sealed class StarredAuthorRefreshService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly ILogger<StarredAuthorRefreshService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;

    public StarredAuthorRefreshService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        ILogger<StarredAuthorRefreshService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("refresh-starred", out var holder))
        {
            error = $"Another task is already running ({holder})";
            return false;
        }
        error = null;
        _isRunning = true;
        _currentMessage = "Starting starred author refresh…";

        _ = Task.Run(async () =>
        {
            try { await RunAsync(hostCt); }
            catch (OperationCanceledException) when (hostCt.IsCancellationRequested)
            {
                _log.LogWarning("Starred author refresh canceled");
                _currentMessage = "Canceled";
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Starred author refresh failed");
                _currentMessage = $"Failed: {ExceptionFormatter.Flatten(ex)}";
            }
            finally { _isRunning = false; _coordinator.Release(); }
        }, hostCt);

        return true;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var refresher = scope.ServiceProvider.GetRequiredService<AuthorRefresher>();

        var authorIds = await db.Authors
            .Where(a => a.Priority >= 1)
            .OrderBy(a => a.Name)
            .Select(a => a.Id)
            .ToListAsync(ct);

        if (authorIds.Count == 0)
        {
            _currentMessage = "No starred authors found";
            return;
        }

        _currentMessage = $"Refreshing 0 / {authorIds.Count} starred author(s)…";
        int done = 0;
        int booksAdded = 0;

        foreach (var id in authorIds)
        {
            ct.ThrowIfCancellationRequested();

            var author = await db.Authors.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (author is null) { done++; continue; }

            _currentMessage = $"Refreshing {author.Name} ({done + 1} / {authorIds.Count})…";

            AuthorRefreshOutcome outcome;
            try
            {
                outcome = await refresher.RefreshAsync(
                    author,
                    msg => _currentMessage = msg,
                    ct);
            }
            catch (AuthorRefreshAlreadyRunningException ex)
            {
                _log.LogInformation(ex,
                    "Skipping starred refresh for author {AuthorId} — already running", id);
                done++;
                continue;
            }

            booksAdded += outcome.BooksAdded;
            done++;
            _currentMessage = $"Refreshed {done} / {authorIds.Count} starred author(s); {booksAdded} new book(s) so far…";
        }

        _currentMessage = $"Done — refreshed {authorIds.Count} starred author(s); {booksAdded} new book(s)";
    }
}

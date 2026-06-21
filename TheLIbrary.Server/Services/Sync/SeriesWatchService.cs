using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Pushover;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

public sealed record SeriesWatchSummary(int NewVolumes, bool Notified);

// Scheduled job (OFF by default): when a series you already own a book in gains a
// new, recently-added volume that you don't own, mark it Wanted and (if Pushover
// is configured) send one summary alert. This is the high-signal new-release case
// — a continuation of a series you're collecting — distinct from the per-author
// "new book" alert the refresher already sends.
public sealed class SeriesWatchService
{
    // A volume counts as "new" if its added-date is within this window.
    private const int WindowDays = 14;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly ILogger<SeriesWatchService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;
    private SeriesWatchSummary? _lastResult;

    public SeriesWatchService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        ILogger<SeriesWatchService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;
    public SeriesWatchSummary? LastResult => _lastResult;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("series-watch", out var holder))
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
            catch (Exception ex) { _log.LogError(ex, "Series-watch failed"); }
            finally { _isRunning = false; _currentMessage = null; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    internal Task<SeriesWatchSummary> RunForTestsAsync(CancellationToken ct) => RunAsync(ct);

    private async Task<SeriesWatchSummary> RunAsync(CancellationToken ct)
    {
        _currentMessage = "Scanning series you own";
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

        // Series the user owns at least one book in.
        var ownedSeries = await db.Books.AsNoTracking()
            .Where(b => b.SeriesId != null)
            .Where(BookOwnership.Owned)
            .Select(b => b.SeriesId!.Value)
            .Distinct()
            .ToListAsync(ct);
        if (ownedSeries.Count == 0) return new SeriesWatchSummary(0, false);

        var ownedSet = ownedSeries.ToHashSet();
        var since = DateTime.UtcNow.AddDays(-WindowDays);

        // Recently-added, unowned, not-yet-wanted volumes in those series.
        var candidates = await db.Books
            .Where(b => b.SeriesId != null && b.CreatedAt != null && b.CreatedAt >= since
                     && !b.Suppressed && !b.Foreign && !b.Wanted)
            .Where(BookOwnership.NotOwned)
            .ToListAsync(ct);

        var toWant = candidates.Where(b => ownedSet.Contains(b.SeriesId!.Value)).ToList();
        if (toWant.Count == 0)
        {
            _currentMessage = "Done — no new series volumes";
            return new SeriesWatchSummary(0, false);
        }

        foreach (var b in toWant) b.Wanted = true;
        await db.SaveChangesAsync(ct);

        var notified = false;
        var pushover = scope.ServiceProvider.GetService<PushoverClient>();
        if (pushover is not null && await pushover.IsConfiguredAsync(ct))
        {
            var sample = string.Join(", ", toWant.Take(5).Select(b => b.Title));
            var msg = $"{toWant.Count} new volume(s) in series you own were added and marked Wanted: {sample}"
                + (toWant.Count > 5 ? "…" : "");
            var r = await pushover.SendAsync("New in your series", msg, null, ct);
            notified = r.Sent;
        }

        _log.LogInformation("Series-watch: marked {Count} new series volume(s) wanted (notified={Notified})", toWant.Count, notified);
        _currentMessage = $"Done — {toWant.Count} new volume(s) marked wanted";
        return new SeriesWatchSummary(toWant.Count, notified);
    }
}

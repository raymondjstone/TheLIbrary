using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

public sealed record PhysicalAuthorStarSummary(int AuthorsUpdated);

// Scheduled job: any author with at least one manually-owned book and a zero
// star rating is bumped to 1 star so physical-only authors don't get left off
// the watchlist.
public sealed class PhysicalAuthorStarService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly ILogger<PhysicalAuthorStarService> _log;
    private volatile bool _isRunning;
    private PhysicalAuthorStarSummary? _lastResult;

    public PhysicalAuthorStarService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        ILogger<PhysicalAuthorStarService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public PhysicalAuthorStarSummary? LastResult => _lastResult;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("star physical-book authors", out var holder))
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
            catch (Exception ex) { _log.LogError(ex, "Physical-author star job failed"); }
            finally { _isRunning = false; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    internal static IReadOnlyList<int> PickAuthorIdsToStarForTests(
        IReadOnlyList<(int Id, int Priority)> authors,
        IReadOnlyList<(int AuthorId, bool ManuallyOwned)> books)
    {
        var physicalAuthorIds = books
            .Where(b => b.ManuallyOwned)
            .Select(b => b.AuthorId)
            .Distinct()
            .ToHashSet();

        return authors
            .Where(a => a.Priority == 0 && physicalAuthorIds.Contains(a.Id))
            .Select(a => a.Id)
            .OrderBy(id => id)
            .ToList();
    }

    private async Task<PhysicalAuthorStarSummary> RunAsync(CancellationToken ct)
    {
        _log.LogInformation("Physical-author star job starting");
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

        var targetIds = await db.Authors
            .Where(a => a.Priority == 0 && db.Books.Any(b => b.AuthorId == a.Id && b.ManuallyOwned))
            .Select(a => a.Id)
            .ToListAsync(ct);

        if (targetIds.Count == 0)
        {
            _log.LogInformation("Physical-author star job found no authors to update");
            return new PhysicalAuthorStarSummary(0);
        }

        await db.Authors
            .Where(a => targetIds.Contains(a.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Priority, _ => 1), ct);

        _log.LogInformation("Physical-author star job set 1 star on {Count} author(s)", targetIds.Count);
        return new PhysicalAuthorStarSummary(targetIds.Count);
    }
}

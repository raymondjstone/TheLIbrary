using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

public sealed record SeriesCoAuthorStarSummary(int AuthorsStarred);

// Scheduled job: when a STARRED author (Priority >= 1) writes for a series that
// ALSO has volumes by other authors, those co-authors — if currently unstarred
// (Priority 0) — are given 1 star so they're tracked for new releases too. A
// co-authored / shared-universe series you care about is usually one you want to
// follow across all its authors. Pure set-based DB work; only ever raises a 0 to
// a 1 (reversible by unstarring).
public sealed class SeriesCoAuthorStarService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly ILogger<SeriesCoAuthorStarService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;
    private SeriesCoAuthorStarSummary? _lastResult;

    public SeriesCoAuthorStarService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        ILogger<SeriesCoAuthorStarService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;
    public SeriesCoAuthorStarSummary? LastResult => _lastResult;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("star series co-authors", out var holder))
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
            catch (Exception ex) { _log.LogError(ex, "Series co-author star job failed"); }
            finally { _isRunning = false; _currentMessage = null; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    // Pure selection logic, mirrored by the DB query, exposed for unit tests:
    // unstarred authors who have a book in a series that also holds a book by a
    // starred author OF A DIFFERENT NAME (a same-name "co-author" is the same person
    // catalogued twice — a duplicate record — not a genuine collaborator).
    internal static IReadOnlyList<int> PickCoAuthorIdsToStar(
        IReadOnlyList<(int Id, string Name, int Priority)> authors,
        IReadOnlyList<(int AuthorId, int? SeriesId)> books)
    {
        var byId = authors.ToDictionary(a => a.Id, a => a);

        // For each series, the distinct names of STARRED authors in it.
        var starredNamesBySeries = new Dictionary<int, HashSet<string>>();
        foreach (var b in books)
        {
            if (b.SeriesId is int sid && byId.TryGetValue(b.AuthorId, out var au) && au.Priority >= 1)
            {
                if (!starredNamesBySeries.TryGetValue(sid, out var set))
                    starredNamesBySeries[sid] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(au.Name);
            }
        }

        var result = new SortedSet<int>();
        foreach (var b in books)
        {
            if (b.SeriesId is not int sid) continue;
            if (!byId.TryGetValue(b.AuthorId, out var au) || au.Priority != 0) continue;
            if (starredNamesBySeries.TryGetValue(sid, out var names)
                && names.Any(n => !string.Equals(n, au.Name, StringComparison.OrdinalIgnoreCase)))
                result.Add(b.AuthorId);
        }
        return result.ToList();
    }

    private async Task<SeriesCoAuthorStarSummary> RunAsync(CancellationToken ct)
    {
        _log.LogInformation("Series co-author star job starting");
        _currentMessage = "Finding co-authors of starred authors' series";
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

        // Unstarred authors with a book in a series that also holds a STARRED
        // author's book. Computed as a plain query (translates reliably), then a
        // single set-based update to 1 star.
        var targetIds = await db.Authors
            .Where(a => a.Priority == 0
                && db.Books.Any(b => b.AuthorId == a.Id && b.SeriesId != null
                    // The starred author sharing the series must have a DIFFERENT
                    // name — a same-name "co-author" is just this author catalogued
                    // twice (a duplicate OL record), not a genuine collaborator, so
                    // starring it only duplicates the books in every list.
                    && db.Books.Any(o => o.SeriesId == b.SeriesId
                        && o.Author.Priority >= 1 && o.Author.Name != a.Name)))
            .Select(a => a.Id)
            .ToListAsync(ct);

        if (targetIds.Count == 0)
        {
            _log.LogInformation("Series co-author star job found no authors to star");
            _currentMessage = "Done — no co-authors to star";
            return new SeriesCoAuthorStarSummary(0);
        }

        await db.Authors
            .Where(a => targetIds.Contains(a.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Priority, _ => 1), ct);

        _log.LogInformation("Series co-author star job gave 1 star to {Count} co-author(s)", targetIds.Count);
        _currentMessage = $"Done — starred {targetIds.Count} co-author(s)";
        return new SeriesCoAuthorStarSummary(targetIds.Count);
    }
}

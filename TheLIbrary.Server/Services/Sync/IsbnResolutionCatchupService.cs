using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

public sealed record IsbnCatchupSummary(int Considered, int Found, int Remaining);

// Scheduled catch-up job (OFF by default): warm the shared IsbnResolutions cache
// for ISBNs that predate the content-scan's inline resolution. Content-scan now
// resolves each file's ISBN as it scans (going forward), but every file scanned
// BEFORE that still has an un-cached ISBN. This walks the distinct ISBNs on
// BookContentScan rows, resolves any not yet in the cache against OpenLibrary
// (one call per unique code — shared by every file that carries it), and stores
// the result so the Identified page never looks them up on demand. DB-only
// candidate selection; each resolution is one OL call, paced by the shared rate
// limiter and capped per run.
public sealed class IsbnResolutionCatchupService
{
    public const int MaxPerRun = 200;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly ILogger<IsbnResolutionCatchupService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;
    private IsbnCatchupSummary? _lastResult;

    public IsbnResolutionCatchupService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        ILogger<IsbnResolutionCatchupService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;
    public IsbnCatchupSummary? LastResult => _lastResult;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("resolve-isbns", out var holder))
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
            catch (Exception ex) { _log.LogError(ex, "ISBN resolution catch-up failed"); }
            finally { _isRunning = false; _currentMessage = null; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    internal Task<IsbnCatchupSummary> RunForTestsAsync(CancellationToken ct) => RunAsync(ct);

    private async Task<IsbnCatchupSummary> RunAsync(CancellationToken ct)
    {
        _currentMessage = "Finding ISBNs with no cached resolution";
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<LibraryDbContext>();
        var resolver = sp.GetRequiredService<IsbnResolutionService>();

        var maxPerRun = await JobRunLimits.GetAsync(db, AppSettingKeys.ResolveIsbnsMaxPerRun, MaxPerRun, ct);

        // Already-cached ISBN keys, and every distinct ISBN on a scan row. Both are
        // DB-only (no NAS reads). Normalize each raw ISBN to its cache key and keep
        // the ones not yet cached — capped per run, the rest reported as remaining.
        var cached = (await db.IsbnResolutions.AsNoTracking().Select(r => r.Isbn).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var rawIsbns = await db.BookContentScans.AsNoTracking()
            .Where(c => c.Isbn != null && c.Isbn != "")
            .Select(c => c.Isbn!)
            .Distinct()
            .ToListAsync(ct);

        var pending = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var totalMissing = 0;
        foreach (var raw in rawIsbns)
        {
            var key = IsbnResolution.IsbnKey(raw);
            if (key is null || cached.Contains(key) || !seen.Add(key)) continue;
            totalMissing++;
            if (pending.Count < maxPerRun) pending.Add(raw);
        }

        int considered = 0, found = 0, deferred = 0;
        foreach (var raw in pending)
        {
            ct.ThrowIfCancellationRequested();
            considered++;
            _currentMessage = $"Resolving ISBN {considered}/{pending.Count}";
            try
            {
                var res = await resolver.ResolveAsync(raw, ct);
                if (res?.Title is not null) found++;   // resolved to a title (OpenLibrary or a fallback)
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (IsbnLookupUnavailableException)
            {
                // A source was rate/quota-capped for THIS ISBN — it's left uncached and
                // re-attempted on a later run. Do NOT abort the whole batch: other ISBNs
                // still resolve via OpenLibrary or a source that isn't capped (and a
                // capped source's own throttle/latch makes its calls cheap), so pressing
                // on gets far more done than stopping at the first blip.
                deferred++;
            }
            catch (Exception ex)
            {
                // One bad ISBN (or a transient error) shouldn't abort the batch either.
                _log.LogWarning(ex, "resolve-isbns: could not resolve ISBN {Isbn}", raw);
                deferred++;
            }
        }

        // Not-reached this run PLUS the ones we attempted but couldn't resolve (left
        // uncached) are all still pending for a later run.
        var remaining = Math.Max(0, totalMissing - considered) + deferred;
        _log.LogInformation(
            "resolve-isbns done — attempted {Considered}, resolved {Found}, deferred {Deferred} (source capped/error), {Remaining} still uncached",
            considered, found, deferred, remaining);
        _currentMessage = $"Done — resolved {found}"
            + (deferred > 0 ? $", {deferred} deferred (retry later)" : "")
            + $", {remaining} remaining";
        return new IsbnCatchupSummary(considered, found, remaining);
    }
}

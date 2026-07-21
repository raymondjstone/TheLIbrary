using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

public sealed record RetryIsbnMissesSummary(int Considered, int Resolved, int StillMissing, int Remaining);

// Scheduled job (OFF by default): reprocesses ISBNs the cache gave up on — rows in
// IsbnResolutions with Title == null (a confirmed miss, or one that hit the
// fail-attempt limit). A source that had nothing before (or was rate/quota-capped at
// the time) may have something now, so this gives the OLDEST misses (by ResolvedAt) a
// fresh shot: delete the stale row, then run the SAME resolution chain (OpenLibrary,
// then Google/Hardcover/LoC/ISBNdb in registration order — see Program.cs) used
// everywhere else. No bespoke lookup logic here, same as the Settings page's "reset
// cached misses" followed by a resolve — just driven directly off IsbnResolutions
// instead of waiting for a BookContentScan walk to rediscover the ISBN.
//
// Gated on Google Books specifically: only runs while a Google Books API key is
// configured, and stops the moment Google's OWN daily quota latches exhausted
// (GoogleBooksRateLimiter.IsExhaustedToday) — checked before every row, no HTTP call
// needed either way. OpenLibrary and the other fallback sources are still consulted
// per row same as always; Google is just the run's stop signal, since it's the only
// source with a reliable daily-exhaustion latch (ISBNdb deliberately has none — see
// IsbndbFallbackProvider's own comment on why guessing there mis-fired twice).
public sealed class IsbnMissRetryService
{
    public const int MaxPerRun = 1000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly GoogleBooksRateLimiter _googleLimiter;
    private readonly ILogger<IsbnMissRetryService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;
    private RetryIsbnMissesSummary? _lastResult;

    public IsbnMissRetryService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        GoogleBooksRateLimiter googleLimiter,
        ILogger<IsbnMissRetryService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _googleLimiter = googleLimiter;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;
    public RetryIsbnMissesSummary? LastResult => _lastResult;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("retry-isbn-misses", out var holder))
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
            catch (Exception ex) { _log.LogError(ex, "ISBN miss retry failed"); }
            // _currentMessage is left holding the "Done — …" summary so the Sync page
            // shows the run's outcome (same as resolve-isbns / dedupe-unknown / etc).
            finally { _isRunning = false; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    internal Task<RetryIsbnMissesSummary> RunForTestsAsync(CancellationToken ct) => RunAsync(ct);

    private async Task<RetryIsbnMissesSummary> RunAsync(CancellationToken ct)
    {
        _currentMessage = "Checking Google Books is configured";
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<LibraryDbContext>();
        var resolver = sp.GetRequiredService<IsbnResolutionService>();

        // "The Google service is active" — gate the WHOLE run on this, not just the
        // stop condition: no key configured means Google was never going to be tried
        // for anything, so there's no point burning OpenLibrary/other-source calls on
        // a reprocessing pass whose whole reason for existing is to give Google a shot.
        var googleKey = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.GoogleBooksApiKey)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(googleKey))
        {
            var stillNull = await db.IsbnResolutions.CountAsync(r => r.Title == null, ct);
            _currentMessage = "Skipped — Google Books isn't configured (Settings → ISBN metadata fallbacks)";
            _log.LogInformation("retry-isbn-misses: skipped, Google Books API key not configured");
            return new RetryIsbnMissesSummary(0, 0, 0, stillNull);
        }
        if (_googleLimiter.IsExhaustedToday)
        {
            var stillNull = await db.IsbnResolutions.CountAsync(r => r.Title == null, ct);
            _currentMessage = "Skipped — Google Books daily quota is already exhausted for today";
            _log.LogInformation("retry-isbn-misses: skipped, Google Books quota already exhausted today");
            return new RetryIsbnMissesSummary(0, 0, 0, stillNull);
        }

        var maxPerRun = await JobRunLimits.GetAsync(db, AppSettingKeys.RetryIsbnMissesMaxPerRun, MaxPerRun, ct);

        _currentMessage = "Finding the oldest unresolved ISBNs";
        var candidates = await db.IsbnResolutions.AsNoTracking()
            .Where(r => r.Title == null)
            .OrderBy(r => r.ResolvedAt)
            .Select(r => r.Isbn)
            .Take(maxPerRun)
            .ToListAsync(ct);

        int considered = 0, resolved = 0, stillMissing = 0, deferred = 0;
        foreach (var isbn in candidates)
        {
            ct.ThrowIfCancellationRequested();

            // Checked before EVERY row, not just once up front — Google's quota can
            // run out mid-run, and this is the run's designated stop signal ("process
            // records until the Google quota is used up").
            if (_googleLimiter.IsExhaustedToday)
            {
                _log.LogInformation(
                    "retry-isbn-misses: stopping — Google Books quota exhausted after {Considered}", considered);
                break;
            }

            considered++;
            _currentMessage = $"Reprocessing ISBN {considered}/{candidates.Count}";
            try
            {
                // Drop the stale miss row first — ResolveAsync short-circuits and
                // returns ANY existing row unchanged, including a null-title one, so
                // without this it would just hand back the same miss. Re-running
                // ResolveAsync then tries OpenLibrary first, same as every other path.
                await db.IsbnResolutions.Where(r => r.Isbn == isbn).ExecuteDeleteAsync(ct);
                var res = await resolver.ResolveAsync(isbn, ct);
                if (res?.Title is not null) resolved++;
                else stillMissing++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // Host shutdown cancelled the in-flight command. SqlClient surfaces
                // this as a SqlException ("A severe error occurred on the current
                // command"), not an OperationCanceledException — so normalize it to
                // cancellation rather than mislogging a perfectly good ISBN as
                // unprocessable (and wrongly counting it as deferred).
                throw new OperationCanceledException(ct);
            }
            catch (IsbnLookupUnavailableException)
            {
                // Nothing resolved and a source was unavailable — the row is gone
                // (deleted above; ResolveAsync doesn't reinsert on this path), so it's
                // picked up again by a later run of this job (or resolve-isbns, once a
                // BookContentScan row still references it).
                deferred++;
            }
            catch (Exception ex)
            {
                // One bad ISBN (or a transient error) shouldn't abort the batch.
                _log.LogWarning(ex, "retry-isbn-misses: could not reprocess ISBN {Isbn}", isbn);
                deferred++;
            }
        }

        var remaining = await db.IsbnResolutions.CountAsync(r => r.Title == null, ct);
        _log.LogInformation(
            "retry-isbn-misses done — considered {Considered}, resolved {Resolved}, still missing {StillMissing}, deferred {Deferred}, {Remaining} null-title rows remain",
            considered, resolved, stillMissing, deferred, remaining);
        _currentMessage = $"Done — resolved {resolved} of {considered} reprocessed"
            + (deferred > 0 ? $", {deferred} deferred (retry later)" : "")
            + $", {remaining} still unresolved";
        return new RetryIsbnMissesSummary(considered, resolved, stillMissing, remaining);
    }
}

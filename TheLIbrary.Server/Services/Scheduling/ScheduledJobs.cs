using TheLibrary.Server.Services.Incoming;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Services.Scheduling;

// Hangfire entry points. Each method takes the process-lifetime cancellation
// token (Hangfire threads it in via its JobActivator), kicks off the
// corresponding singleton service, and then polls IsRunning until the job
// completes. The polling is what keeps the Hangfire worker slot held for the
// entire duration — with WorkerCount=1, that's what delivers mutual exclusion
// between scheduled jobs.
//
// Manual UI clicks go through the controllers and reach the same TryStart*
// APIs; if a scheduled job is already holding the BackgroundTaskCoordinator
// the manual click returns 409 and vice versa.
public sealed class ScheduledJobs
{
    private readonly SyncService _sync;
    private readonly IncomingService _incoming;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ScheduledJobs> _log;

    public ScheduledJobs(SyncService sync, IncomingService incoming, IHostApplicationLifetime lifetime, ILogger<ScheduledJobs> log)
    {
        _sync = sync; _incoming = incoming; _lifetime = lifetime; _log = log;
    }

    public Task RunSync() => RunWithPolling(
        ScheduleJobIds.Sync,
        ct => _sync.TryStart(ct, out var err) ? (true, err) : (false, err),
        () => _sync.IsRunning);

    public Task RunSeed() => RunWithPolling(
        ScheduleJobIds.Seed,
        ct => _sync.TryStartSeed(ct, out var err) ? (true, err) : (false, err),
        () => _sync.IsRunning);

    public Task RunAuthorUpdates() => RunWithPolling(
        ScheduleJobIds.AuthorUpdates,
        ct => _sync.TryStartAuthorUpdates(ct, out var err) ? (true, err) : (false, err),
        () => _sync.IsRunning);

    public Task RunIncoming() => RunWithPolling(
        ScheduleJobIds.Incoming,
        ct => _incoming.TryStart(ct, out var err) ? (true, err) : (false, err),
        () => _incoming.IsRunning);

    public Task RunReprocessUnknown() => RunWithPolling(
        ScheduleJobIds.ReprocessUnknown,
        ct => _incoming.TryStartUnknown(ct, out var err) ? (true, err) : (false, err),
        () => _incoming.IsRunning);

    private async Task RunWithPolling(
        string jobId,
        Func<CancellationToken, (bool started, string? error)> start,
        Func<bool> isRunning)
    {
        var ct = _lifetime.ApplicationStopping;
        _log.LogInformation("Scheduled job {Job} starting", jobId);

        // Wait for the coordinator to free up rather than failing fast. A
        // manual UI trigger can race a cron firing; the scheduled job should
        // just queue behind it. Ceiling prevents an indefinite stuck-hold
        // from silently blocking the Hangfire worker forever.
        var waitCeiling = TimeSpan.FromHours(2);
        var waited = TimeSpan.Zero;
        var poll = TimeSpan.FromSeconds(10);
        (bool started, string? error) outcome;
        while (true)
        {
            outcome = start(ct);
            if (outcome.started) break;
            if (waited >= waitCeiling)
            {
                _log.LogWarning("Scheduled job {Job} gave up after {Waited}: {Error}",
                    jobId, waited, outcome.error);
                throw new InvalidOperationException(
                    $"Could not start {jobId} within {waitCeiling}: {outcome.error}");
            }
            _log.LogInformation("Scheduled job {Job} waiting for coordinator: {Error}",
                jobId, outcome.error);
            await Task.Delay(poll, ct);
            waited += poll;
        }

        while (isRunning())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        _log.LogInformation("Scheduled job {Job} finished", jobId);
    }
}

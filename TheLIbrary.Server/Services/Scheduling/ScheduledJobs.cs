using Hangfire;
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
// Manual UI clicks set manualTrigger=true, which bypasses the schedule-enabled
// check. Recurring jobs leave it at the default (false), so any stale enqueued
// instance that fires after the schedule is disabled is silently discarded.
public sealed class ScheduledJobs
{
    private readonly SyncService _sync;
    private readonly IncomingService _incoming;
    private readonly SeriesOrganizerService _organizer;
    private readonly UnzipService _unzip;
    private readonly AuthorFolderDisambiguatorService _disambiguator;
    private readonly SameNameAuthorService _sameNames;
    private readonly ScheduleService _schedules;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ScheduledJobs> _log;

    public ScheduledJobs(
        SyncService sync,
        IncomingService incoming,
        SeriesOrganizerService organizer,
        UnzipService unzip,
        AuthorFolderDisambiguatorService disambiguator,
        SameNameAuthorService sameNames,
        ScheduleService schedules,
        IHostApplicationLifetime lifetime,
        ILogger<ScheduledJobs> log)
    {
        _sync = sync; _incoming = incoming; _organizer = organizer; _unzip = unzip;
        _disambiguator = disambiguator; _sameNames = sameNames;
        _schedules = schedules; _lifetime = lifetime; _log = log;
    }

    [AutomaticRetry(Attempts = 0)]
    public Task RunSync(bool manualTrigger = false) => RunWithPolling(
        ScheduleJobIds.Sync, manualTrigger,
        ct => _sync.TryStart(ct, out var err) ? (true, err) : (false, err),
        () => _sync.IsRunning);

    [AutomaticRetry(Attempts = 0)]
    public Task RunSeed(bool manualTrigger = false) => RunWithPolling(
        ScheduleJobIds.Seed, manualTrigger,
        ct => _sync.TryStartSeed(ct, out var err) ? (true, err) : (false, err),
        () => _sync.IsRunning);

    [AutomaticRetry(Attempts = 0)]
    public Task RunAuthorUpdates(bool manualTrigger = false) => RunWithPolling(
        ScheduleJobIds.AuthorUpdates, manualTrigger,
        ct => _sync.TryStartAuthorUpdates(ct, out var err) ? (true, err) : (false, err),
        () => _sync.IsRunning);

    [AutomaticRetry(Attempts = 0)]
    public Task RunIncoming(bool manualTrigger = false) => RunWithPolling(
        ScheduleJobIds.Incoming, manualTrigger,
        ct => _incoming.TryStart(ct, out var err) ? (true, err) : (false, err),
        () => _incoming.IsRunning);

    [AutomaticRetry(Attempts = 0)]
    public Task RunReprocessUnknown(bool manualTrigger = false) => RunWithPolling(
        ScheduleJobIds.ReprocessUnknown, manualTrigger,
        ct => _incoming.TryStartUnknown(ct, out var err) ? (true, err) : (false, err),
        () => _incoming.IsRunning);

    [AutomaticRetry(Attempts = 0)]
    public Task RunRefreshDueWorks(bool manualTrigger = false) => RunWithPolling(
        ScheduleJobIds.RefreshWorks, manualTrigger,
        ct => _sync.TryStartRefreshDueWorks(ct, out var err) ? (true, err) : (false, err),
        () => _sync.IsRunning);

    [AutomaticRetry(Attempts = 0)]
    public Task RunOrganizeSeries(bool manualTrigger = false) => RunWithPolling(
        ScheduleJobIds.OrganizeSeries, manualTrigger,
        ct => _organizer.TryStart(ct, out var err) ? (true, err) : (false, err),
        () => _organizer.IsRunning);

    [AutomaticRetry(Attempts = 0)]
    public Task RunUnzip(bool manualTrigger = false) => RunWithPolling(
        ScheduleJobIds.Unzip, manualTrigger,
        ct => _unzip.TryStart(ct, out var err) ? (true, err) : (false, err),
        () => _unzip.IsRunning);

    [AutomaticRetry(Attempts = 0)]
    public Task RunDisambiguateFolders(bool manualTrigger = false) => RunWithPolling(
        ScheduleJobIds.DisambiguateFolders, manualTrigger,
        ct => _disambiguator.TryStart(ct, out var err) ? (true, err) : (false, err),
        () => _disambiguator.IsRunning);

    [AutomaticRetry(Attempts = 0)]
    public Task RunSameNameAuthors(bool manualTrigger = false) => RunWithPolling(
        ScheduleJobIds.SameNameAuthors, manualTrigger,
        ct => _sameNames.TryStart(ct, out var err) ? (true, err) : (false, err),
        () => _sameNames.IsRunning);

    private async Task RunWithPolling(
        string jobId,
        bool manualTrigger,
        Func<CancellationToken, (bool started, string? error)> start,
        Func<bool> isRunning)
    {
        var ct = _lifetime.ApplicationStopping;

        // Stale enqueued instances (from when the schedule was enabled, or from
        // a retry loop) must not run after the user disables the schedule.
        // Manual triggers bypass this so the user can always fire on demand.
        if (!manualTrigger)
        {
            var all = await _schedules.GetAllAsync(ct);
            if (!all.TryGetValue(jobId, out var entry) || !entry.Enabled)
            {
                _log.LogInformation(
                    "Scheduled job {Job} is disabled — discarding stale enqueued run", jobId);
                return;
            }
        }

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
                return;
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

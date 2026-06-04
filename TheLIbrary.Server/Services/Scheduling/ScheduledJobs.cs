using Hangfire;
using TheLibrary.Server.Services.Incoming;
using TheLibrary.Server.Services.OpenLibrary;
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
    internal static Func<int>? HangfireQueueLengthProviderOverride;

    private readonly SyncService _sync;
    private readonly IncomingService _incoming;
    private readonly SeriesOrganizerService _organizer;
    private readonly UnzipService _unzip;
    private readonly AuthorFolderDisambiguatorService _disambiguator;
    private readonly SameNameAuthorService _sameNames;
    private readonly PhysicalAuthorStarService _physicalStars;
    private readonly OpenLibraryMetadataCacheService _metadataCache;
    private readonly UnknownFolderFlattenerService _flattenUnknown;
    private readonly UnknownAuthorAdoptionService _adoptUnknownAuthors;
    private readonly ForeignArchiveService _archiveForeign;
    private readonly LinkedAuthorMergeService _mergeLinkedAuthors;
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
        PhysicalAuthorStarService physicalStars,
        OpenLibraryMetadataCacheService metadataCache,
        UnknownFolderFlattenerService flattenUnknown,
        UnknownAuthorAdoptionService adoptUnknownAuthors,
        ForeignArchiveService archiveForeign,
        LinkedAuthorMergeService mergeLinkedAuthors,
        ScheduleService schedules,
        IHostApplicationLifetime lifetime,
        ILogger<ScheduledJobs> log)
    {
        _sync = sync; _incoming = incoming; _organizer = organizer; _unzip = unzip;
        _disambiguator = disambiguator; _sameNames = sameNames; _physicalStars = physicalStars; _metadataCache = metadataCache;
        _flattenUnknown = flattenUnknown; _adoptUnknownAuthors = adoptUnknownAuthors; _archiveForeign = archiveForeign;
        _mergeLinkedAuthors = mergeLinkedAuthors;
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
    public Task RunRefreshDueWorks(bool manualTrigger = false)
    {
        if (!manualTrigger && GetTotalHangfireQueueLength() > 3)
        {
            _log.LogInformation(
                "Scheduled job {Job} skipped because the Hangfire queue length is above the threshold",
                ScheduleJobIds.RefreshWorks);
            return Task.CompletedTask;
        }

        return RunWithPolling(
            ScheduleJobIds.RefreshWorks, manualTrigger,
            ct => _sync.TryStartRefreshDueWorks(ct, out var err) ? (true, err) : (false, err),
            () => _sync.IsRunning);
    }

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

    [AutomaticRetry(Attempts = 0)]
    public Task RunStarPhysicalAuthors(bool manualTrigger = false) => RunWithPolling(
        ScheduleJobIds.StarPhysicalAuthors, manualTrigger,
        ct => _physicalStars.TryStart(ct, out var err) ? (true, err) : (false, err),
        () => _physicalStars.IsRunning);

    [AutomaticRetry(Attempts = 0)]
    public Task RunCacheOpenLibraryMetadata(bool manualTrigger = false) => RunWithPolling(
        ScheduleJobIds.CacheOpenLibraryMetadata, manualTrigger,
        ct => _metadataCache.TryStart(ct, out var err) ? (true, err) : (false, err),
        () => _metadataCache.IsRunning);

    [AutomaticRetry(Attempts = 0)]
    public Task RunFlattenUnknown(bool manualTrigger = false) => RunWithPolling(
        ScheduleJobIds.FlattenUnknown, manualTrigger,
        ct => _flattenUnknown.TryStart(ct, out var err) ? (true, err) : (false, err),
        () => _flattenUnknown.IsRunning);

    [AutomaticRetry(Attempts = 0)]
    public Task RunAdoptUnknownAuthors(bool manualTrigger = false) => RunWithPolling(
        ScheduleJobIds.AdoptUnknownAuthors, manualTrigger,
        ct => _adoptUnknownAuthors.TryStart(ct, out var err) ? (true, err) : (false, err),
        () => _adoptUnknownAuthors.IsRunning);

    [AutomaticRetry(Attempts = 0)]
    public Task RunArchiveForeign(bool manualTrigger = false) => RunWithPolling(
        ScheduleJobIds.ArchiveForeign, manualTrigger,
        ct => _archiveForeign.TryStart(ct, out var err) ? (true, err) : (false, err),
        () => _archiveForeign.IsRunning);

    [AutomaticRetry(Attempts = 0)]
    public Task RunMergeLinkedAuthors(bool manualTrigger = false) => RunWithPolling(
        ScheduleJobIds.MergeLinkedAuthors, manualTrigger,
        ct => _mergeLinkedAuthors.TryStart(ct, out var err) ? (true, err) : (false, err),
        () => _mergeLinkedAuthors.IsRunning);

    internal Task RunWithPollingForTests(
        IReadOnlyDictionary<string, ScheduleEntry> schedules,
        string jobId,
        bool manualTrigger,
        Func<CancellationToken, (bool started, string? error)> start,
        Func<bool> isRunning,
        TimeSpan waitCeiling,
        TimeSpan poll,
        CancellationToken ct)
        => RunWithPollingCore(schedules, jobId, manualTrigger, start, isRunning, waitCeiling, poll, ct);

    private async Task RunWithPolling(
        string jobId,
        bool manualTrigger,
        Func<CancellationToken, (bool started, string? error)> start,
        Func<bool> isRunning)
    {
        var ct = _lifetime.ApplicationStopping;
        var schedules = await _schedules.GetAllAsync(ct);
        await RunWithPollingCore(
            schedules,
            jobId,
            manualTrigger,
            start,
            isRunning,
            TimeSpan.FromHours(2),
            TimeSpan.FromSeconds(10),
            ct);
    }

    private async Task RunWithPollingCore(
        IReadOnlyDictionary<string, ScheduleEntry> schedules,
        string jobId,
        bool manualTrigger,
        Func<CancellationToken, (bool started, string? error)> start,
        Func<bool> isRunning,
        TimeSpan waitCeiling,
        TimeSpan poll,
        CancellationToken ct)
    {

        // Stale enqueued instances (from when the schedule was enabled, or from
        // a retry loop) must not run after the user disables the schedule.
        // Manual triggers bypass this so the user can always fire on demand.
        if (!manualTrigger)
        {
            if (!schedules.TryGetValue(jobId, out var entry) || !entry.Enabled)
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
        var waited = TimeSpan.Zero;
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

    private static int GetTotalHangfireQueueLength()
    {
        try
        {
            if (HangfireQueueLengthProviderOverride is not null)
                return HangfireQueueLengthProviderOverride();

            var queues = JobStorage.Current.GetMonitoringApi().Queues();
            return (int)Math.Min(int.MaxValue, queues.Sum(q => Math.Max(0L, q.Length)));
        }
        catch
        {
            return 0;
        }
    }
}

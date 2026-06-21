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
    private readonly UnknownDuplicateRemovalService _dedupeUnknown;
    private readonly AuthorDuplicateRemovalService _dedupeAuthorFiles;
    private readonly ManualBookPromotionService _promoteManualBooks;
    private readonly UnknownAuthorAdoptionService _adoptUnknownAuthors;
    private readonly ForeignArchiveService _archiveForeign;
    private readonly LinkedAuthorMergeService _mergeLinkedAuthors;
    private readonly BookIntegrityService _integrity;
    private readonly StaleFileCleanupService _pruneStaleFiles;
    private readonly ContentScanService _contentScan;
    private readonly UntrackedAuthorAssignmentService _assignAuthors;
    private readonly TheLibrary.Server.Services.Search.FullTextSearchService _fullText;
    private readonly AuthorPruneService _pruneAuthors;
    private readonly DuplicateAutoArchiveService _dupAutoArchive;
    private readonly SeriesWatchService _seriesWatch;
    private readonly TheLibrary.Server.Services.Download.AutoReplaceDamagedService _autoReplaceDamaged;
    private readonly WorkResolutionService _resolveWorks;
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
        UnknownDuplicateRemovalService dedupeUnknown,
        AuthorDuplicateRemovalService dedupeAuthorFiles,
        ManualBookPromotionService promoteManualBooks,
        UnknownAuthorAdoptionService adoptUnknownAuthors,
        ForeignArchiveService archiveForeign,
        LinkedAuthorMergeService mergeLinkedAuthors,
        BookIntegrityService integrity,
        StaleFileCleanupService pruneStaleFiles,
        ContentScanService contentScan,
        UntrackedAuthorAssignmentService assignAuthors,
        TheLibrary.Server.Services.Search.FullTextSearchService fullText,
        AuthorPruneService pruneAuthors,
        DuplicateAutoArchiveService dupAutoArchive,
        SeriesWatchService seriesWatch,
        TheLibrary.Server.Services.Download.AutoReplaceDamagedService autoReplaceDamaged,
        WorkResolutionService resolveWorks,
        ScheduleService schedules,
        IHostApplicationLifetime lifetime,
        ILogger<ScheduledJobs> log)
    {
        _sync = sync; _incoming = incoming; _organizer = organizer; _unzip = unzip;
        _disambiguator = disambiguator; _sameNames = sameNames; _physicalStars = physicalStars; _metadataCache = metadataCache;
        _flattenUnknown = flattenUnknown; _dedupeUnknown = dedupeUnknown; _dedupeAuthorFiles = dedupeAuthorFiles; _promoteManualBooks = promoteManualBooks; _adoptUnknownAuthors = adoptUnknownAuthors; _archiveForeign = archiveForeign;
        _mergeLinkedAuthors = mergeLinkedAuthors; _integrity = integrity; _pruneStaleFiles = pruneStaleFiles;
        _contentScan = contentScan; _assignAuthors = assignAuthors; _fullText = fullText; _pruneAuthors = pruneAuthors;
        _dupAutoArchive = dupAutoArchive; _seriesWatch = seriesWatch; _autoReplaceDamaged = autoReplaceDamaged; _resolveWorks = resolveWorks;
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
    public Task RunDedupeUnknown(bool manualTrigger = false) => RunWithPolling(
        ScheduleJobIds.DedupeUnknown, manualTrigger,
        ct => _dedupeUnknown.TryStart(ct, out var err) ? (true, err) : (false, err),
        () => _dedupeUnknown.IsRunning);

    [AutomaticRetry(Attempts = 0)]
    public Task RunDedupeAuthorFiles(bool manualTrigger = false) => RunWithPolling(
        ScheduleJobIds.DedupeAuthorFiles, manualTrigger,
        ct => _dedupeAuthorFiles.TryStart(ct, out var err) ? (true, err) : (false, err),
        () => _dedupeAuthorFiles.IsRunning);

    [AutomaticRetry(Attempts = 0)]
    public Task RunPromoteManualBooks(bool manualTrigger = false) => RunWithPolling(
        ScheduleJobIds.PromoteManualBooks, manualTrigger,
        ct => _promoteManualBooks.TryStart(ct, out var err) ? (true, err) : (false, err),
        () => _promoteManualBooks.IsRunning);

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

    [AutomaticRetry(Attempts = 0)]
    public Task RunCheckIntegrity(bool manualTrigger = false) => RunWithPolling(
        ScheduleJobIds.CheckIntegrity, manualTrigger,
        ct => _integrity.TryStart(ct, out var err) ? (true, err) : (false, err),
        () => _integrity.IsRunning);

    [AutomaticRetry(Attempts = 0)]
    public Task RunPruneStaleFiles(bool manualTrigger = false) => RunWithPolling(
        ScheduleJobIds.PruneStaleFiles, manualTrigger,
        ct => _pruneStaleFiles.TryStart(ct, out var err) ? (true, err) : (false, err),
        () => _pruneStaleFiles.IsRunning);

    [AutomaticRetry(Attempts = 0)]
    public Task RunContentScan(bool manualTrigger = false) => RunWithPolling(
        ScheduleJobIds.ContentScan, manualTrigger,
        ct => _contentScan.TryStart(ct, out var err) ? (true, err) : (false, err),
        () => _contentScan.IsRunning);

    [AutomaticRetry(Attempts = 0)]
    public Task RunAssignAuthors(bool manualTrigger = false)
    {
        // Fires every 15 minutes by default — when a long job (sync, integrity)
        // holds the single Hangfire worker, the missed firings pile up in the
        // queue. Skip when the queue is already backed up rather than burning
        // the backlog one stale instance at a time.
        if (!manualTrigger && GetTotalHangfireQueueLength() > 3)
        {
            _log.LogInformation(
                "Scheduled job {Job} skipped because the Hangfire queue length is above the threshold",
                ScheduleJobIds.AssignAuthors);
            return Task.CompletedTask;
        }

        return RunWithPolling(
            ScheduleJobIds.AssignAuthors, manualTrigger,
            ct => _assignAuthors.TryStart(ct, out var err) ? (true, err) : (false, err),
            () => _assignAuthors.IsRunning);
    }

    [AutomaticRetry(Attempts = 0)]
    public Task RunIndexFullText(bool manualTrigger = false) => RunWithPolling(
        ScheduleJobIds.IndexFullText, manualTrigger,
        ct => _fullText.TryStart(ct, out var err) ? (true, err) : (false, err),
        () => _fullText.IsRunning);

    [AutomaticRetry(Attempts = 0)]
    public Task RunPruneAuthors(bool manualTrigger = false) => RunWithPolling(
        ScheduleJobIds.PruneAuthors, manualTrigger,
        ct => _pruneAuthors.TryStart(ct, out var err) ? (true, err) : (false, err),
        () => _pruneAuthors.IsRunning);

    [AutomaticRetry(Attempts = 0)]
    public Task RunDuplicateAutoArchive(bool manualTrigger = false) => RunWithPolling(
        ScheduleJobIds.DuplicateAutoArchive, manualTrigger,
        ct => _dupAutoArchive.TryStart(ct, out var err) ? (true, err) : (false, err),
        () => _dupAutoArchive.IsRunning);

    [AutomaticRetry(Attempts = 0)]
    public Task RunSeriesWatch(bool manualTrigger = false) => RunWithPolling(
        ScheduleJobIds.SeriesWatch, manualTrigger,
        ct => _seriesWatch.TryStart(ct, out var err) ? (true, err) : (false, err),
        () => _seriesWatch.IsRunning);

    [AutomaticRetry(Attempts = 0)]
    public Task RunAutoReplaceDamaged(bool manualTrigger = false) => RunWithPolling(
        ScheduleJobIds.AutoReplaceDamaged, manualTrigger,
        ct => _autoReplaceDamaged.TryStart(ct, out var err) ? (true, err) : (false, err),
        () => _autoReplaceDamaged.IsRunning);

    [AutomaticRetry(Attempts = 0)]
    public Task RunResolveWorks(bool manualTrigger = false) => RunWithPolling(
        ScheduleJobIds.ResolveWorks, manualTrigger,
        ct => _resolveWorks.TryStart(ct, out var err) ? (true, err) : (false, err),
        () => _resolveWorks.IsRunning);

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
            WaitCeilingFor(manualTrigger),
            TimeSpan.FromSeconds(10),
            ct);
    }

    // How long a firing will sit waiting for the coordinator before giving up —
    // and, crucially, how long it pins the SINGLE Hangfire worker while it waits.
    // The old flat 2-hour ceiling was a starvation footgun: when one task held the
    // coordinator (a long sync, or a stuck/never-released UI-triggered run), the
    // next cron firing would occupy the only worker for up to 2 hours polling for
    // the lock, and every other scheduled job queued behind it did nothing.
    //
    // A cron firing now waits only briefly (a brief race against a UI trigger
    // resolves; a genuinely busy/stuck holder makes it give up fast and free the
    // worker — the next cron tick retries once the coordinator is free). A manual
    // trigger, which the user explicitly asked to run, gets a longer but still
    // bounded window so it can queue behind a short in-flight task.
    internal static TimeSpan WaitCeilingFor(bool manualTrigger)
        => manualTrigger ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(2);

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

        // Wait briefly for the coordinator to free up. This loop holds the SINGLE
        // Hangfire worker the whole time it polls, so the ceiling is deliberately
        // short for cron firings (see WaitCeilingFor): a long/stuck coordinator
        // holder must not let one waiting job pin the only worker and starve every
        // other scheduled job. Giving up frees the worker; the next cron tick
        // retries. Manual triggers get a longer window so they can queue behind a
        // short in-flight task.
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

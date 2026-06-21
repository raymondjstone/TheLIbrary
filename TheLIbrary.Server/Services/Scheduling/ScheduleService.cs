using System.Text.Json;
using Hangfire;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;

namespace TheLibrary.Server.Services.Scheduling;

// Loads/saves the schedule config from AppSettings and pushes it into
// Hangfire's recurring-job registry. Kept as a singleton that builds its
// own scope per operation — same pattern the other singleton services use.
public sealed class ScheduleService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly IRecurringJobManager _recurring;
    private readonly ILogger<ScheduleService> _log;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public ScheduleService(IServiceScopeFactory scopeFactory, IBackgroundJobClient backgroundJobs, IRecurringJobManager recurring, ILogger<ScheduleService> log)
    {
        _scopeFactory = scopeFactory;
        _backgroundJobs = backgroundJobs;
        _recurring = recurring;
        _log = log;
    }

    public async Task<IReadOnlyDictionary<string, ScheduleEntry>> GetAllAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var cfg = await LoadConfigAsync(db, ct);
        // Ensure every known job id is represented so the client always
        // sees the full roster even on a fresh install.
        var merged = new Dictionary<string, ScheduleEntry>(StringComparer.Ordinal);
        foreach (var id in ScheduleJobIds.All)
        {
            // Persisted config wins; otherwise the built-in default. A missing
            // default must never crash startup — fall back to a disabled daily
            // entry so a newly-added job id can't take the whole app down.
            merged[id] = cfg.Jobs.TryGetValue(id, out var e)
                ? e
                : ScheduleJobIds.Defaults.TryGetValue(id, out var d)
                    ? Clone(d)
                    : new ScheduleEntry { Cron = "0 3 * * *", Enabled = false };
        }
        return merged;
    }

    public async Task<ScheduleEntry> UpdateAsync(string jobId, ScheduleEntry entry, CancellationToken ct = default)
    {
        if (!ScheduleJobIds.All.Contains(jobId))
            throw new ArgumentException($"Unknown job id '{jobId}'", nameof(jobId));
        if (string.IsNullOrWhiteSpace(entry.Cron))
            throw new ArgumentException("Cron expression is required", nameof(entry));

        var clean = new ScheduleEntry { Cron = entry.Cron.Trim(), Enabled = entry.Enabled };

        // Push to Hangfire first so a bad cron throws before we persist.
        // If Hangfire accepts it (or the job is disabled), save to AppSettings.
        ApplyOne(jobId, clean);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var cfg = await LoadConfigAsync(db, ct);
        cfg.Jobs[jobId] = clean;
        await SaveConfigAsync(db, cfg, ct);

        return clean;
    }

    // Called at startup and whenever we want to fully resync. Every known
    // job id is pushed through ApplyOne; disabled ones get their Hangfire
    // registration removed, enabled ones (re)scheduled.
    public async Task ApplyAllAsync(CancellationToken ct = default)
    {
        var all = await GetAllAsync(ct);
        foreach (var (jobId, entry) in all) ApplyOne(jobId, entry);
    }

    // Fires a job right now via Hangfire's BackgroundJob.Enqueue path so it
    // runs under the same single-worker queue and respects the coordinator.
    public string TriggerNow(string jobId)
    {
        return jobId switch
        {
            ScheduleJobIds.Sync => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunSync(true)),
            ScheduleJobIds.Seed => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunSeed(true)),
            ScheduleJobIds.AuthorUpdates => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunAuthorUpdates(true)),
            ScheduleJobIds.Incoming => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunIncoming(true)),
            ScheduleJobIds.ReprocessUnknown => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunReprocessUnknown(true)),
            ScheduleJobIds.RefreshWorks => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunRefreshDueWorks(true)),
            ScheduleJobIds.OrganizeSeries => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunOrganizeSeries(true)),
            ScheduleJobIds.Unzip => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunUnzip(true)),
            ScheduleJobIds.DisambiguateFolders => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunDisambiguateFolders(true)),
            ScheduleJobIds.SameNameAuthors => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunSameNameAuthors(true)),
            ScheduleJobIds.StarPhysicalAuthors => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunStarPhysicalAuthors(true)),
            ScheduleJobIds.CacheOpenLibraryMetadata => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunCacheOpenLibraryMetadata(true)),
            ScheduleJobIds.FlattenUnknown => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunFlattenUnknown(true)),
            ScheduleJobIds.DedupeUnknown => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunDedupeUnknown(true)),
            ScheduleJobIds.DedupeAuthorFiles => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunDedupeAuthorFiles(true)),
            ScheduleJobIds.DuplicateAutoArchive => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunDuplicateAutoArchive(true)),
            ScheduleJobIds.SeriesWatch => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunSeriesWatch(true)),
            ScheduleJobIds.AutoReplaceDamaged => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunAutoReplaceDamaged(true)),
            ScheduleJobIds.ResolveWorks => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunResolveWorks(true)),
            ScheduleJobIds.PromoteManualBooks => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunPromoteManualBooks(true)),
            ScheduleJobIds.AdoptUnknownAuthors => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunAdoptUnknownAuthors(true)),
            ScheduleJobIds.ArchiveForeign => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunArchiveForeign(true)),
            ScheduleJobIds.MergeLinkedAuthors => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunMergeLinkedAuthors(true)),
            ScheduleJobIds.CheckIntegrity => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunCheckIntegrity(true)),
            ScheduleJobIds.PruneStaleFiles => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunPruneStaleFiles(true)),
            ScheduleJobIds.ContentScan => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunContentScan(true)),
            ScheduleJobIds.AssignAuthors => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunAssignAuthors(true)),
            _ => throw new ArgumentException($"Unknown job id '{jobId}'", nameof(jobId)),
        };
    }

    public IReadOnlyDictionary<string, DateTime?> GetNextRuns()
    {
        using var connection = JobStorage.Current.GetConnection();
        var all = connection.GetRecurringJobs();
        var map = new Dictionary<string, DateTime?>(StringComparer.Ordinal);
        foreach (var id in ScheduleJobIds.All)
        {
            var job = all.FirstOrDefault(j => j.Id == id);
            map[id] = job?.NextExecution;
        }
        return map;
    }

    public Task<int> ClearFailedJobsAsync(CancellationToken ct = default)
    {
        const int pageSize = 1000;
        int deleted = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var batch = JobStorage.Current.GetMonitoringApi().FailedJobs(0, pageSize);
            if (batch.Count == 0) break;

            foreach (var jobId in batch.Select(x => x.Key).ToList())
            {
                ct.ThrowIfCancellationRequested();
                if (_backgroundJobs.Delete(jobId))
                    deleted++;
            }

            if (batch.Count < pageSize) break;
        }

        if (deleted > 0)
            _log.LogInformation("Removed {Count} failed Hangfire job(s) during startup cleanup", deleted);

        return Task.FromResult(deleted);
    }

    private void ApplyOne(string jobId, ScheduleEntry entry)
    {
        if (!entry.Enabled)
        {
            _recurring.RemoveIfExists(jobId);
            _log.LogInformation("Schedule {Job}: disabled — recurring job removed", jobId);
            return;
        }

        try
        {
            switch (jobId)
            {
                case ScheduleJobIds.Sync:
                    _recurring.AddOrUpdate<ScheduledJobs>(jobId, j => j.RunSync(), entry.Cron);
                    break;
                case ScheduleJobIds.Seed:
                    _recurring.AddOrUpdate<ScheduledJobs>(jobId, j => j.RunSeed(), entry.Cron);
                    break;
                case ScheduleJobIds.AuthorUpdates:
                    _recurring.AddOrUpdate<ScheduledJobs>(jobId, j => j.RunAuthorUpdates(), entry.Cron);
                    break;
                case ScheduleJobIds.Incoming:
                    _recurring.AddOrUpdate<ScheduledJobs>(jobId, j => j.RunIncoming(), entry.Cron);
                    break;
                case ScheduleJobIds.ReprocessUnknown:
                    _recurring.AddOrUpdate<ScheduledJobs>(jobId, j => j.RunReprocessUnknown(), entry.Cron);
                    break;
                case ScheduleJobIds.RefreshWorks:
                    _recurring.AddOrUpdate<ScheduledJobs>(jobId, j => j.RunRefreshDueWorks(), entry.Cron);
                    break;
                case ScheduleJobIds.OrganizeSeries:
                    _recurring.AddOrUpdate<ScheduledJobs>(jobId, j => j.RunOrganizeSeries(), entry.Cron);
                    break;
                case ScheduleJobIds.Unzip:
                    _recurring.AddOrUpdate<ScheduledJobs>(jobId, j => j.RunUnzip(), entry.Cron);
                    break;
                case ScheduleJobIds.DisambiguateFolders:
                    _recurring.AddOrUpdate<ScheduledJobs>(jobId, j => j.RunDisambiguateFolders(), entry.Cron);
                    break;
                case ScheduleJobIds.SameNameAuthors:
                    _recurring.AddOrUpdate<ScheduledJobs>(jobId, j => j.RunSameNameAuthors(), entry.Cron);
                    break;
                case ScheduleJobIds.StarPhysicalAuthors:
                    _recurring.AddOrUpdate<ScheduledJobs>(jobId, j => j.RunStarPhysicalAuthors(), entry.Cron);
                    break;
                case ScheduleJobIds.CacheOpenLibraryMetadata:
                    _recurring.AddOrUpdate<ScheduledJobs>(jobId, j => j.RunCacheOpenLibraryMetadata(), entry.Cron);
                    break;
                case ScheduleJobIds.FlattenUnknown:
                    _recurring.AddOrUpdate<ScheduledJobs>(jobId, j => j.RunFlattenUnknown(), entry.Cron);
                    break;
                case ScheduleJobIds.DedupeUnknown:
                    _recurring.AddOrUpdate<ScheduledJobs>(jobId, j => j.RunDedupeUnknown(), entry.Cron);
                    break;
                case ScheduleJobIds.DedupeAuthorFiles:
                    _recurring.AddOrUpdate<ScheduledJobs>(jobId, j => j.RunDedupeAuthorFiles(), entry.Cron);
                    break;
                case ScheduleJobIds.DuplicateAutoArchive:
                    _recurring.AddOrUpdate<ScheduledJobs>(jobId, j => j.RunDuplicateAutoArchive(), entry.Cron);
                    break;
                case ScheduleJobIds.SeriesWatch:
                    _recurring.AddOrUpdate<ScheduledJobs>(jobId, j => j.RunSeriesWatch(), entry.Cron);
                    break;
                case ScheduleJobIds.AutoReplaceDamaged:
                    _recurring.AddOrUpdate<ScheduledJobs>(jobId, j => j.RunAutoReplaceDamaged(), entry.Cron);
                    break;
                case ScheduleJobIds.ResolveWorks:
                    _recurring.AddOrUpdate<ScheduledJobs>(jobId, j => j.RunResolveWorks(), entry.Cron);
                    break;
                case ScheduleJobIds.PromoteManualBooks:
                    _recurring.AddOrUpdate<ScheduledJobs>(jobId, j => j.RunPromoteManualBooks(), entry.Cron);
                    break;
                case ScheduleJobIds.AdoptUnknownAuthors:
                    _recurring.AddOrUpdate<ScheduledJobs>(jobId, j => j.RunAdoptUnknownAuthors(), entry.Cron);
                    break;
                case ScheduleJobIds.ArchiveForeign:
                    _recurring.AddOrUpdate<ScheduledJobs>(jobId, j => j.RunArchiveForeign(), entry.Cron);
                    break;
                case ScheduleJobIds.MergeLinkedAuthors:
                    _recurring.AddOrUpdate<ScheduledJobs>(jobId, j => j.RunMergeLinkedAuthors(), entry.Cron);
                    break;
                case ScheduleJobIds.CheckIntegrity:
                    _recurring.AddOrUpdate<ScheduledJobs>(jobId, j => j.RunCheckIntegrity(), entry.Cron);
                    break;
                case ScheduleJobIds.PruneStaleFiles:
                    _recurring.AddOrUpdate<ScheduledJobs>(jobId, j => j.RunPruneStaleFiles(), entry.Cron);
                    break;
                case ScheduleJobIds.ContentScan:
                    _recurring.AddOrUpdate<ScheduledJobs>(jobId, j => j.RunContentScan(), entry.Cron);
                    break;
                case ScheduleJobIds.AssignAuthors:
                    _recurring.AddOrUpdate<ScheduledJobs>(jobId, j => j.RunAssignAuthors(), entry.Cron);
                    break;
                case ScheduleJobIds.IndexFullText:
                    _recurring.AddOrUpdate<ScheduledJobs>(jobId, j => j.RunIndexFullText(), entry.Cron);
                    break;
                case ScheduleJobIds.PruneAuthors:
                    _recurring.AddOrUpdate<ScheduledJobs>(jobId, j => j.RunPruneAuthors(), entry.Cron);
                    break;
            }
            _log.LogInformation("Schedule {Job}: enabled with cron '{Cron}'", jobId, entry.Cron);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to register schedule for {Job}", jobId);
            throw;
        }
    }

    private static async Task<ScheduleConfig> LoadConfigAsync(LibraryDbContext db, CancellationToken ct)
    {
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == AppSettingKeys.Schedules, ct);
        if (row is null || string.IsNullOrWhiteSpace(row.Value)) return new ScheduleConfig();
        try
        {
            return JsonSerializer.Deserialize<ScheduleConfig>(row.Value, JsonOpts) ?? new ScheduleConfig();
        }
        catch
        {
            return new ScheduleConfig();
        }
    }

    private static async Task SaveConfigAsync(LibraryDbContext db, ScheduleConfig cfg, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(cfg, JsonOpts);
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == AppSettingKeys.Schedules, ct);
        if (row is null)
            db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.Schedules, Value = json });
        else
            row.Value = json;
        await db.SaveChangesAsync(ct);
    }

    private static ScheduleEntry Clone(ScheduleEntry e) => new() { Cron = e.Cron, Enabled = e.Enabled };
}

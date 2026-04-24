using System.Text.Json;
using Hangfire;
using Hangfire.Storage;
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
    private readonly IRecurringJobManager _recurring;
    private readonly ILogger<ScheduleService> _log;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public ScheduleService(IServiceScopeFactory scopeFactory, IRecurringJobManager recurring, ILogger<ScheduleService> log)
    {
        _scopeFactory = scopeFactory;
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
            merged[id] = cfg.Jobs.TryGetValue(id, out var e) ? e : Clone(ScheduleJobIds.Defaults[id]);
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
            ScheduleJobIds.Sync => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunSync()),
            ScheduleJobIds.Seed => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunSeed()),
            ScheduleJobIds.AuthorUpdates => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunAuthorUpdates()),
            ScheduleJobIds.Incoming => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunIncoming()),
            ScheduleJobIds.ReprocessUnknown => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunReprocessUnknown()),
            ScheduleJobIds.RefreshWorks => BackgroundJob.Enqueue<ScheduledJobs>(j => j.RunRefreshDueWorks()),
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

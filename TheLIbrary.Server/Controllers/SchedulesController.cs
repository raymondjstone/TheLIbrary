using Hangfire.Storage.Monitoring;
using Microsoft.AspNetCore.Mvc;
using TheLibrary.Server.Services.Scheduling;
using Hangfire;

namespace TheLibrary.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SchedulesController : ControllerBase
{
    private readonly ScheduleService _schedules;

    public SchedulesController(ScheduleService schedules) { _schedules = schedules; }

    public sealed record ScheduleDto(string JobId, string Cron, bool Enabled, DateTime? NextRunUtc);

    [HttpGet]
    public async Task<ActionResult<IEnumerable<ScheduleDto>>> List(CancellationToken ct)
    {
        var entries = await _schedules.GetAllAsync(ct);
        var nextRuns = _schedules.GetNextRuns();
        var list = entries.Select(kv =>
            new ScheduleDto(kv.Key, kv.Value.Cron, kv.Value.Enabled,
                nextRuns.TryGetValue(kv.Key, out var n) ? n : null));
        return Ok(list);
    }

    public sealed record UpdateRequest(string Cron, bool Enabled);

    [HttpPut("{jobId}")]
    public async Task<IActionResult> Update(string jobId, [FromBody] UpdateRequest req, CancellationToken ct)
    {
        try
        {
            var result = await _schedules.UpdateAsync(jobId, new ScheduleEntry { Cron = req.Cron, Enabled = req.Enabled }, ct);
            return Ok(new ScheduleDto(jobId, result.Cron, result.Enabled,
                _schedules.GetNextRuns().TryGetValue(jobId, out var n) ? n : null));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{jobId}/run")]
    public IActionResult RunNow(string jobId)
    {
        try
        {
            var id = _schedules.TriggerNow(jobId);
            return Accepted(new { hangfireId = id });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    public sealed record JobRunDto(string State, DateTime? StartedAt, DateTime? FinishedAt, double? DurationSeconds, string? ExceptionMessage);

    // Returns the last 5 succeeded + last 5 failed runs for the given job id,
    // ordered most-recent first. Uses Hangfire's monitoring API (no extra storage needed).
    [HttpGet("{jobId}/history")]
    public ActionResult<IEnumerable<JobRunDto>> History(string jobId)
    {
        var api = JobStorage.Current.GetMonitoringApi();
        var results = new List<JobRunDto>();

        // Succeeded jobs
        long total = api.SucceededListCount();
        long fetched = 0;
        const int page = 50;
        while (fetched < total && results.Count < 5)
        {
            var batch = api.SucceededJobs((int)Math.Min(fetched, int.MaxValue), page);
            if (batch.Count == 0) break;
            foreach (var kv in batch)
            {
                if (results.Count >= 5) break;
                var job = kv.Value;
                // Hangfire doesn't expose the recurring job id on the succeeded
                // entry directly, so we match by method name heuristic.
                var methodName = job.Job?.Method?.Name ?? "";
                if (!JobIdMatchesMethod(jobId, methodName)) continue;
                double? dur = job.TotalDuration;
                results.Add(new JobRunDto("Succeeded",
                    job.SucceededAt?.AddSeconds(-(dur ?? 0)),
                    job.SucceededAt,
                    dur, null));
            }
            fetched += batch.Count;
        }

        // Failed jobs
        var failedBatch = api.FailedJobs(0, 50);
        var failedResults = new List<JobRunDto>();
        foreach (var kv in failedBatch)
        {
            if (failedResults.Count >= 5) break;
            var job = kv.Value;
            var methodName = job.Job?.Method?.Name ?? "";
            if (!JobIdMatchesMethod(jobId, methodName)) continue;
            failedResults.Add(new JobRunDto("Failed",
                job.FailedAt, job.FailedAt, null, job.ExceptionMessage));
        }

        var all = results.Concat(failedResults)
            .OrderByDescending(r => r.FinishedAt ?? r.StartedAt)
            .Take(10)
            .ToList();
        return Ok(all);
    }

    private static bool JobIdMatchesMethod(string jobId, string methodName)
    {
        // Map job ids to the expected Hangfire method names on ScheduledJobs.
        return jobId switch
        {
            ScheduleJobIds.Sync => methodName == "RunSync",
            ScheduleJobIds.Seed => methodName == "RunSeed",
            ScheduleJobIds.AuthorUpdates => methodName == "RunAuthorUpdates",
            ScheduleJobIds.Incoming => methodName == "RunIncoming",
            ScheduleJobIds.ReprocessUnknown => methodName == "RunReprocessUnknown",
            ScheduleJobIds.RefreshWorks => methodName == "RunRefreshDueWorks",
            ScheduleJobIds.OrganizeSeries => methodName == "RunOrganizeSeries",
            ScheduleJobIds.Unzip => methodName == "RunUnzip",
            ScheduleJobIds.DisambiguateFolders => methodName == "RunDisambiguateFolders",
            ScheduleJobIds.SameNameAuthors => methodName == "RunSameNameAuthors",
            ScheduleJobIds.StarPhysicalAuthors => methodName == "RunStarPhysicalAuthors",
            ScheduleJobIds.CacheOpenLibraryMetadata => methodName == "RunCacheOpenLibraryMetadata",
            ScheduleJobIds.FlattenUnknown => methodName == "RunFlattenUnknown",
            ScheduleJobIds.AdoptUnknownAuthors => methodName == "RunAdoptUnknownAuthors",
            _ => false,
        };
    }
}

using Microsoft.AspNetCore.Mvc;
using TheLibrary.Server.Services.Scheduling;

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
}

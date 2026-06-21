using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;

namespace TheLibrary.Server.Controllers;

// Read-only audit trail of consequential file actions (archive / delete /
// auto-archive). Answers "what moved, when, and why" without log-trawling.
[ApiController]
[Route("api/activity")]
public sealed class ActivityController : ControllerBase
{
    private readonly LibraryDbContext _db;
    public ActivityController(LibraryDbContext db) { _db = db; }

    public sealed record ActivityRow(int Id, DateTime At, string Action, string Source, string Detail, int? BookId);

    [HttpGet]
    public async Task<IReadOnlyList<ActivityRow>> Get([FromQuery] int take = 200, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 1000);
        return await _db.ActivityLog.AsNoTracking()
            .OrderByDescending(e => e.At).ThenByDescending(e => e.Id)
            .Take(take)
            .Select(e => new ActivityRow(e.Id, e.At, e.Action, e.Source, e.Detail, e.BookId))
            .ToListAsync(ct);
    }
}

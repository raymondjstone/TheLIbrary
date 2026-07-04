using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Controllers;

[ApiController]
[Route("api/title-blacklist")]
public class TitleBlacklistController : ControllerBase
{
    private readonly LibraryDbContext _db;
    public TitleBlacklistController(LibraryDbContext db) { _db = db; }

    public sealed record BlacklistDto(int Id, string Title, string NormalizedTitle, DateTime AddedAt);

    [HttpGet]
    public async Task<IReadOnlyList<BlacklistDto>> List(CancellationToken ct)
    {
        return await _db.TitleBlacklist.AsNoTracking()
            .OrderBy(x => x.Title)
            .Select(x => new BlacklistDto(x.Id, x.Title, x.NormalizedTitle, x.AddedAt))
            .ToListAsync(ct);
    }

    public sealed record AddRequest(string Title);

    [HttpPost]
    public async Task<ActionResult<BlacklistDto>> Add([FromBody] AddRequest body, CancellationToken ct)
    {
        var title = body.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title))
            return BadRequest(new { error = "Title is required" });

        var normalized = TitleNormalizer.Normalize(title);
        if (string.IsNullOrEmpty(normalized))
            return BadRequest(new { error = "Title is required" });

        if (await _db.TitleBlacklist.AnyAsync(x => x.NormalizedTitle == normalized, ct))
            return Conflict(new { error = "Already blacklisted" });

        var row = new TitleBlacklist
        {
            Title = title,
            NormalizedTitle = normalized,
            AddedAt = DateTime.UtcNow,
        };
        _db.TitleBlacklist.Add(row);
        await _db.SaveChangesAsync(ct);
        return new BlacklistDto(row.Id, row.Title, row.NormalizedTitle, row.AddedAt);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Remove(int id, CancellationToken ct)
    {
        var row = await _db.TitleBlacklist.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return NotFound();
        _db.TitleBlacklist.Remove(row);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}

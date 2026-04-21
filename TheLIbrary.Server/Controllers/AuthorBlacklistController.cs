using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Controllers;

[ApiController]
[Route("api/author-blacklist")]
public class AuthorBlacklistController : ControllerBase
{
    private readonly LibraryDbContext _db;
    public AuthorBlacklistController(LibraryDbContext db) { _db = db; }

    public sealed record BlacklistDto(int Id, string Name, string NormalizedName, string? FolderName, DateTime AddedAt, string? Reason);

    [HttpGet]
    public async Task<IReadOnlyList<BlacklistDto>> List(CancellationToken ct)
    {
        return await _db.AuthorBlacklist.AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x => new BlacklistDto(x.Id, x.Name, x.NormalizedName, x.FolderName, x.AddedAt, x.Reason))
            .ToListAsync(ct);
    }

    public sealed record AddRequest(string Name, string? FolderName, string? Reason);

    [HttpPost]
    public async Task<ActionResult<BlacklistDto>> Add([FromBody] AddRequest body, CancellationToken ct)
    {
        var name = body.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { error = "Name is required" });

        var normalized = TitleNormalizer.NormalizeAuthor(name);
        if (string.IsNullOrEmpty(normalized))
            return BadRequest(new { error = "Name is required" });

        if (await _db.AuthorBlacklist.AnyAsync(x => x.NormalizedName == normalized, ct))
            return Conflict(new { error = "Already blacklisted" });

        var row = new AuthorBlacklist
        {
            Name = name,
            NormalizedName = normalized,
            FolderName = string.IsNullOrWhiteSpace(body.FolderName) ? null : body.FolderName.Trim(),
            Reason = string.IsNullOrWhiteSpace(body.Reason) ? null : body.Reason.Trim(),
            AddedAt = DateTime.UtcNow,
        };
        _db.AuthorBlacklist.Add(row);
        await _db.SaveChangesAsync(ct);
        return new BlacklistDto(row.Id, row.Name, row.NormalizedName, row.FolderName, row.AddedAt, row.Reason);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Remove(int id, CancellationToken ct)
    {
        var row = await _db.AuthorBlacklist.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return NotFound();
        _db.AuthorBlacklist.Remove(row);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;

namespace TheLibrary.Server.Controllers;

[ApiController]
[Route("api/ignored-folders")]
public class IgnoredFoldersController : ControllerBase
{
    private readonly LibraryDbContext _db;
    public IgnoredFoldersController(LibraryDbContext db) { _db = db; }

    public sealed record IgnoredFolderDto(int Id, string Name, DateTime CreatedAt);

    [HttpGet]
    public async Task<IReadOnlyList<IgnoredFolderDto>> List(CancellationToken ct)
    {
        return await _db.IgnoredFolders.AsNoTracking()
            .OrderBy(f => f.Name)
            .Select(f => new IgnoredFolderDto(f.Id, f.Name, f.CreatedAt))
            .ToListAsync(ct);
    }

    public sealed record AddRequest(string Name);

    [HttpPost]
    public async Task<ActionResult<IgnoredFolderDto>> Add([FromBody] AddRequest body, CancellationToken ct)
    {
        var name = body.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { error = "Name is required" });

        if (await _db.IgnoredFolders.AnyAsync(f => f.Name == name, ct))
            return Conflict(new { error = "Already ignored" });

        var row = new IgnoredFolder { Name = name, CreatedAt = DateTime.UtcNow };
        _db.IgnoredFolders.Add(row);
        await _db.SaveChangesAsync(ct);
        return new IgnoredFolderDto(row.Id, row.Name, row.CreatedAt);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Remove(int id, CancellationToken ct)
    {
        var row = await _db.IgnoredFolders.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (row is null) return NotFound();
        _db.IgnoredFolders.Remove(row);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}

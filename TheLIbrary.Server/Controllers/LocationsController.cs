using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;

namespace TheLibrary.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class LocationsController : ControllerBase
{
    private readonly LibraryDbContext _db;
    public LocationsController(LibraryDbContext db) { _db = db; }

    public sealed record LocationDto(
        int Id, string? Label, string Path, bool Enabled, bool IsPrimary,
        bool Exists, DateTime CreatedAt, DateTime? LastScanAt);

    public sealed record UpsertLocation(string? Label, string Path, bool Enabled);

    [HttpGet]
    public async Task<IReadOnlyList<LocationDto>> List(CancellationToken ct)
    {
        var rows = await _db.LibraryLocations.AsNoTracking()
            .OrderBy(l => l.CreatedAt).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<LocationDto>> Create([FromBody] UpsertLocation body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Path))
            return BadRequest(new { error = "Path is required" });

        var path = body.Path.Trim();
        if (await _db.LibraryLocations.AnyAsync(l => l.Path == path, ct))
            return Conflict(new { error = "A location with that path already exists" });

        var loc = new LibraryLocation
        {
            Label = body.Label?.Trim(),
            Path = path,
            Enabled = body.Enabled,
            CreatedAt = DateTime.UtcNow
        };
        _db.LibraryLocations.Add(loc);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(List), new { id = loc.Id }, ToDto(loc));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<LocationDto>> Update(int id, [FromBody] UpsertLocation body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Path))
            return BadRequest(new { error = "Path is required" });

        var loc = await _db.LibraryLocations.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (loc is null) return NotFound();

        var path = body.Path.Trim();
        if (path != loc.Path && await _db.LibraryLocations.AnyAsync(l => l.Path == path, ct))
            return Conflict(new { error = "A location with that path already exists" });

        loc.Label = body.Label?.Trim();
        loc.Path = path;
        loc.Enabled = body.Enabled;
        await _db.SaveChangesAsync(ct);
        return ToDto(loc);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var loc = await _db.LibraryLocations.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (loc is null) return NotFound();
        _db.LibraryLocations.Remove(loc);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // Marks one location primary and clears the flag on every other row in a
    // single SaveChanges so we never transiently have two or zero primaries.
    [HttpPut("{id:int}/primary")]
    public async Task<IActionResult> SetPrimary(int id, CancellationToken ct)
    {
        var loc = await _db.LibraryLocations.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (loc is null) return NotFound();

        await _db.LibraryLocations
            .Where(l => l.IsPrimary && l.Id != id)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.IsPrimary, _ => false), ct);

        loc.IsPrimary = true;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static LocationDto ToDto(LibraryLocation l) => new(
        l.Id, l.Label, l.Path, l.Enabled, l.IsPrimary,
        Directory.Exists(l.Path), l.CreatedAt, l.LastScanAt);
}

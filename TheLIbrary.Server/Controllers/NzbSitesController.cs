using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;

namespace TheLibrary.Server.Controllers;

[ApiController]
[Route("api/nzb-sites")]
public class NzbSitesController : ControllerBase
{
    private readonly LibraryDbContext _db;
    public NzbSitesController(LibraryDbContext db) => _db = db;

    public sealed record NzbSiteDto(int Id, string Name, string UrlTemplate, int Order, bool Active);
    public sealed record SaveRequest(string Name, string UrlTemplate, int Order, bool Active);

    [HttpGet]
    public async Task<IReadOnlyList<NzbSiteDto>> List(CancellationToken ct)
    {
        var rows = await _db.NzbSites.AsNoTracking()
            .OrderBy(s => s.Order).ThenBy(s => s.Name)
            .ToListAsync(ct);
        return rows.Select(s => new NzbSiteDto(s.Id, s.Name, s.UrlTemplate, s.Order, s.Active)).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<NzbSiteDto>> Add([FromBody] SaveRequest body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
            return BadRequest(new { error = "Name is required" });
        if (string.IsNullOrWhiteSpace(body.UrlTemplate))
            return BadRequest(new { error = "UrlTemplate is required" });

        var site = new NzbSite
        {
            Name = body.Name.Trim(),
            UrlTemplate = body.UrlTemplate.Trim(),
            Order = body.Order,
            Active = body.Active
        };
        _db.NzbSites.Add(site);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(List), new NzbSiteDto(site.Id, site.Name, site.UrlTemplate, site.Order, site.Active));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<NzbSiteDto>> Update(int id, [FromBody] SaveRequest body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
            return BadRequest(new { error = "Name is required" });
        if (string.IsNullOrWhiteSpace(body.UrlTemplate))
            return BadRequest(new { error = "UrlTemplate is required" });

        var site = await _db.NzbSites.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (site is null) return NotFound();

        site.Name = body.Name.Trim();
        site.UrlTemplate = body.UrlTemplate.Trim();
        site.Order = body.Order;
        site.Active = body.Active;
        await _db.SaveChangesAsync(ct);
        return new NzbSiteDto(site.Id, site.Name, site.UrlTemplate, site.Order, site.Active);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var site = await _db.NzbSites.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (site is null) return NotFound();
        _db.NzbSites.Remove(site);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}

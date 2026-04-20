using Microsoft.AspNetCore.Mvc;
using TheLibrary.Server.Services.OpenLibrary;

namespace TheLibrary.Server.Controllers;

[ApiController]
[Route("api/openlibrary")]
public class OpenLibraryController : ControllerBase
{
    private readonly OpenLibraryClient _ol;
    public OpenLibraryController(OpenLibraryClient ol) { _ol = ol; }

    public sealed record AuthorSearchRow(
        string Key,
        string Name,
        string? TopWork,
        int? WorkCount,
        string? BirthDate,
        string? DeathDate);

    [HttpGet("search-authors")]
    public async Task<ActionResult<IReadOnlyList<AuthorSearchRow>>> SearchAuthors(
        [FromQuery] string q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
            return new List<AuthorSearchRow>();

        var resp = await _ol.SearchAuthorsAsync(q.Trim(), ct);
        if (resp?.Docs is null) return new List<AuthorSearchRow>();

        return resp.Docs
            .Where(d => !string.IsNullOrWhiteSpace(d.Key) && !string.IsNullOrWhiteSpace(d.Name))
            .Select(d => new AuthorSearchRow(
                d.Key!, d.Name!, d.TopWork, d.WorkCount, d.BirthDate, d.DeathDate))
            .ToList();
    }
}

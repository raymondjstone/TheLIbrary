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

    public sealed record WorkSearchRow(
        string Key,
        string Title,
        int? FirstPublishYear,
        int? CoverId,
        string? Authors,
        string? PrimaryAuthorKey,
        string? PrimaryAuthorName);

    [HttpGet("search-authors")]
    public async Task<ActionResult<IReadOnlyList<AuthorSearchRow>>> SearchAuthors(
        [FromQuery] string q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
            return new List<AuthorSearchRow>();

        AuthorSearchResponse? resp;
        try
        {
            resp = await _ol.SearchAuthorsAsync(q.Trim(), ct);
        }
        catch (OpenLibraryRequestFailedException ex)
        {
            return Problem(
                title: "OpenLibrary request failed",
                detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (resp?.Docs is null) return new List<AuthorSearchRow>();

        return resp.Docs
            .Where(d => !string.IsNullOrWhiteSpace(d.Key) && !string.IsNullOrWhiteSpace(d.Name))
            .Select(d => new AuthorSearchRow(
                d.Key!, d.Name!, d.TopWork, d.WorkCount, d.BirthDate, d.DeathDate))
            .ToList();
    }

    [HttpGet("search-works")]
    public async Task<ActionResult<IReadOnlyList<WorkSearchRow>>> SearchWorks(
        [FromQuery] string title,
        [FromQuery] string? author,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new List<WorkSearchRow>();

        WorkSearchResponse? resp;
        try
        {
            resp = await _ol.SearchWorksAsync(title.Trim(), author?.Trim(), ct);
        }
        catch (OpenLibraryRequestFailedException ex)
        {
            return Problem(
                title: "OpenLibrary request failed",
                detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (resp?.Docs is null) return new List<WorkSearchRow>();

        return resp.Docs
            .Where(d => !string.IsNullOrWhiteSpace(d.Key) && !string.IsNullOrWhiteSpace(d.Title))
            .Select(d => new WorkSearchRow(
                d.Key!,
                d.Title!,
                d.FirstPublishYear,
                d.CoverId,
                d.AuthorNames is { Count: > 0 } ? string.Join(", ", d.AuthorNames.Where(n => !string.IsNullOrWhiteSpace(n))) : null,
                d.AuthorKeys?.FirstOrDefault(k => !string.IsNullOrWhiteSpace(k)),
                d.AuthorNames?.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))))
            .ToList();
    }
}

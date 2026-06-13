using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.OpenLibrary;

namespace TheLibrary.Server.Controllers;

[ApiController]
[Route("api/openlibrary")]
public class OpenLibraryController : ControllerBase
{
    private readonly OpenLibraryClient _ol;
    private readonly LibraryDbContext _db;
    public OpenLibraryController(OpenLibraryClient ol, LibraryDbContext db) { _ol = ol; _db = db; }

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

        var limit = await GetSearchLimitAsync(ct);

        AuthorSearchResponse? resp;
        try
        {
            resp = await _ol.SearchAuthorsAsync(q.Trim(), ct, limit);
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

        var limit = await GetSearchLimitAsync(ct);

        WorkSearchResponse? resp;
        try
        {
            resp = await _ol.SearchWorksAsync(title.Trim(), author?.Trim(), ct, limit);
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

    private async Task<int> GetSearchLimitAsync(CancellationToken ct)
    {
        var raw = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.OlSearchResultsLimit)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        return int.TryParse(raw, out var n) && n > 0 ? n : OpenLibraryClient.DefaultSearchLimit;
    }
}

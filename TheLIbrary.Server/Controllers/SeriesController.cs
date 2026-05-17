using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SeriesController : ControllerBase
{
    private readonly LibraryDbContext _db;
    public SeriesController(LibraryDbContext db) { _db = db; }

    public sealed record SeriesSummary(int Id, string Name, int? PrimaryAuthorId, string? PrimaryAuthorName);

    public sealed record AuthorRef(int Id, string Name);

    // Lightweight list for dropdowns — no book details.
    [HttpGet]
    public async Task<IReadOnlyList<SeriesSummary>> List(CancellationToken ct) =>
        await _db.Series
            .OrderBy(s => s.Name)
            .Select(s => new SeriesSummary(
                s.Id, s.Name, s.PrimaryAuthorId,
                s.PrimaryAuthor != null ? s.PrimaryAuthor.Name : null))
            .ToListAsync(ct);

    public sealed record SeriesDetailResponse(
        int Id,
        string Name,
        string NormalizedName,
        int? PrimaryAuthorId,
        string? PrimaryAuthorName,
        List<AuthorRef> AdditionalAuthors);

    [HttpGet("{id:int}")]
    public async Task<ActionResult<SeriesDetailResponse>> Get(int id, CancellationToken ct)
    {
        var s = await _db.Series
            .Include(x => x.PrimaryAuthor)
            .Include(x => x.SeriesAuthors).ThenInclude(sa => sa.Author)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound();
        return Ok(ToResponse(s));
    }

    public sealed record UpdateSeriesRequest(
        string Name,
        int? PrimaryAuthorId,
        List<int>? AdditionalAuthorIds);

    [HttpPut("{id:int}")]
    public async Task<ActionResult<SeriesDetailResponse>> Update(int id, [FromBody] UpdateSeriesRequest body, CancellationToken ct)
    {
        var s = await _db.Series
            .Include(x => x.PrimaryAuthor)
            .Include(x => x.SeriesAuthors).ThenInclude(sa => sa.Author)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound();

        if (!string.IsNullOrWhiteSpace(body.Name))
        {
            s.Name = body.Name.Trim();
            s.NormalizedName = TitleNormalizer.Normalize(s.Name);
        }

        s.PrimaryAuthorId = body.PrimaryAuthorId;

        // Sync SeriesAuthors join table
        if (body.AdditionalAuthorIds is not null)
        {
            var wanted = body.AdditionalAuthorIds.ToHashSet();
            var existing = s.SeriesAuthors.Select(sa => sa.AuthorId).ToHashSet();

            foreach (var remove in s.SeriesAuthors.Where(sa => !wanted.Contains(sa.AuthorId)).ToList())
                s.SeriesAuthors.Remove(remove);

            foreach (var add in wanted.Where(aid => !existing.Contains(aid)))
                s.SeriesAuthors.Add(new SeriesAuthor { SeriesId = id, AuthorId = add });
        }

        await _db.SaveChangesAsync(ct);

        // Reload to get author names for any newly added entries
        await _db.Entry(s).Collection(x => x.SeriesAuthors).Query()
            .Include(sa => sa.Author).LoadAsync(ct);
        if (s.PrimaryAuthorId.HasValue && s.PrimaryAuthor is null)
            await _db.Entry(s).Reference(x => x.PrimaryAuthor).LoadAsync(ct);

        return Ok(ToResponse(s));
    }

    private static SeriesDetailResponse ToResponse(Series s) => new(
        s.Id, s.Name, s.NormalizedName, s.PrimaryAuthorId, s.PrimaryAuthor?.Name,
        s.SeriesAuthors.Select(sa => new AuthorRef(sa.AuthorId, sa.Author?.Name ?? "")).ToList());
}

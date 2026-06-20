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

    public sealed record SeriesSummary(
        int Id, string Name,
        int? PrimaryAuthorId, string? PrimaryAuthorName,
        int? ParentSeriesId, string? ParentSeriesName, string? PositionInParent,
        bool GapsInSequence = false,
        string? GapsDescription = null);

    public sealed record AuthorRef(int Id, string Name);
    public sealed record ChildSeriesRef(int Id, string Name, string? PositionInParent);

    public sealed record CreateSeriesRequest(
        string Name,
        int? PrimaryAuthorId,
        int? ParentSeriesId,
        string? PositionInParent);

    [HttpPost]
    public async Task<ActionResult<SeriesDetailResponse>> Create([FromBody] CreateSeriesRequest body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
            return BadRequest(new { error = "Name is required." });

        var name = body.Name.Trim();
        var normalized = TitleNormalizer.Normalize(name);

        if (await _db.Series.AnyAsync(s => s.NormalizedName == normalized, ct))
            return Conflict(new { error = $"A series named \"{name}\" already exists." });

        if (body.ParentSeriesId.HasValue)
        {
            var err = await ValidateParent(-1, body.ParentSeriesId.Value, ct);
            if (err is not null) return BadRequest(new { error = err });
        }

        var series = new Series
        {
            Name = name,
            NormalizedName = normalized,
            PrimaryAuthorId = body.PrimaryAuthorId,
            ParentSeriesId = body.ParentSeriesId,
            PositionInParent = string.IsNullOrWhiteSpace(body.PositionInParent) ? null : body.PositionInParent.Trim()
        };

        _db.Series.Add(series);
        await _db.SaveChangesAsync(ct);

        if (series.PrimaryAuthorId.HasValue)
            await _db.Entry(series).Reference(x => x.PrimaryAuthor).LoadAsync(ct);
        if (series.ParentSeriesId.HasValue)
            await _db.Entry(series).Reference(x => x.ParentSeries).LoadAsync(ct);

        return CreatedAtAction(nameof(Get), new { id = series.Id }, ToResponse(series));
    }

    [HttpGet]
    public async Task<IReadOnlyList<SeriesSummary>> List(CancellationToken ct) =>
        await _db.Series
            .OrderBy(s => s.Name)
            .Select(s => new SeriesSummary(
                s.Id, s.Name,
                s.PrimaryAuthorId, s.PrimaryAuthor != null ? s.PrimaryAuthor.Name : null,
                s.ParentSeriesId, s.ParentSeries != null ? s.ParentSeries.Name : null,
                s.PositionInParent))
            .ToListAsync(ct);

    public sealed record SeriesDetailResponse(
        int Id,
        string Name,
        string NormalizedName,
        int? PrimaryAuthorId,
        string? PrimaryAuthorName,
        int? ParentSeriesId,
        string? ParentSeriesName,
        string? PositionInParent,
        List<AuthorRef> AdditionalAuthors,
        List<ChildSeriesRef> ChildSeries);

    [HttpGet("{id:int}")]
    public async Task<ActionResult<SeriesDetailResponse>> Get(int id, CancellationToken ct)
    {
        var s = await _db.Series
            .Include(x => x.PrimaryAuthor)
            .Include(x => x.ParentSeries)
            .Include(x => x.ChildSeries)
            .Include(x => x.SeriesAuthors).ThenInclude(sa => sa.Author)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound();
        return Ok(ToResponse(s));
    }

    public sealed record UpdateSeriesRequest(
        string Name,
        int? PrimaryAuthorId,
        List<int>? AdditionalAuthorIds,
        int? ParentSeriesId,
        string? PositionInParent);

    [HttpPut("{id:int}")]
    public async Task<ActionResult<SeriesDetailResponse>> Update(int id, [FromBody] UpdateSeriesRequest body, CancellationToken ct)
    {
        var s = await _db.Series
            .Include(x => x.PrimaryAuthor)
            .Include(x => x.ParentSeries)
            .Include(x => x.ChildSeries)
            .Include(x => x.SeriesAuthors).ThenInclude(sa => sa.Author)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound();

        if (!string.IsNullOrWhiteSpace(body.Name))
        {
            s.Name = body.Name.Trim();
            s.NormalizedName = TitleNormalizer.Normalize(s.Name);
        }

        s.PrimaryAuthorId = body.PrimaryAuthorId;
        s.PositionInParent = string.IsNullOrWhiteSpace(body.PositionInParent) ? null : body.PositionInParent.Trim();

        // Validate and set parent
        if (body.ParentSeriesId.HasValue)
        {
            if (body.ParentSeriesId.Value == id)
                return BadRequest(new { error = "A series cannot be its own parent." });

            var err = await ValidateParent(id, body.ParentSeriesId.Value, ct);
            if (err is not null) return BadRequest(new { error = err });

            s.ParentSeriesId = body.ParentSeriesId.Value;
        }
        else
        {
            s.ParentSeriesId = null;
        }

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

        // Reload to get names for newly added entries
        await _db.Entry(s).Collection(x => x.SeriesAuthors).Query()
            .Include(sa => sa.Author).LoadAsync(ct);
        if (s.PrimaryAuthorId.HasValue && s.PrimaryAuthor is null)
            await _db.Entry(s).Reference(x => x.PrimaryAuthor).LoadAsync(ct);
        if (s.ParentSeriesId.HasValue && s.ParentSeries is null)
            await _db.Entry(s).Reference(x => x.ParentSeries).LoadAsync(ct);
        await _db.Entry(s).Collection(x => x.ChildSeries).LoadAsync(ct);

        return Ok(ToResponse(s));
    }

    // Walk up the ancestor chain; reject if 'proposedParentId' is a descendant of 'id'
    // (cycle) or if setting this parent would make 'id' deeper than MaxDepth.
    private const int MaxDepth = 5;

    private async Task<string?> ValidateParent(int id, int proposedParentId, CancellationToken ct)
    {
        // Walk up from proposedParentId; detect cycle and measure depth
        var current = proposedParentId;
        var depth = 1; // id itself will be at depth+1 from root
        while (true)
        {
            var row = await _db.Series
                .Where(s => s.Id == current)
                .Select(s => new { s.ParentSeriesId, Depth = 1 })
                .FirstOrDefaultAsync(ct);

            if (row is null) break; // reached a root

            if (row.ParentSeriesId == id)
                return "Cannot set a descendant series as the parent (would create a cycle).";

            if (row.ParentSeriesId is null) break;

            depth++;
            if (depth >= MaxDepth)
                return $"Maximum series nesting depth ({MaxDepth}) would be exceeded.";

            current = row.ParentSeriesId.Value;
        }

        // Also ensure id's subtree won't exceed MaxDepth
        var subtreeDepth = await GetSubtreeDepth(id, ct);
        if (depth + subtreeDepth > MaxDepth)
            return $"Maximum series nesting depth ({MaxDepth}) would be exceeded.";

        return null;
    }

    private async Task<int> GetSubtreeDepth(int id, CancellationToken ct)
    {
        var children = await _db.Series
            .Where(s => s.ParentSeriesId == id)
            .Select(s => s.Id)
            .ToListAsync(ct);

        if (children.Count == 0) return 1;

        var maxChildDepth = 0;
        foreach (var childId in children)
        {
            var d = await GetSubtreeDepth(childId, ct);
            if (d > maxChildDepth) maxChildDepth = d;
        }
        return 1 + maxChildDepth;
    }

    public sealed record SeriesCompletion(
        int Id, string Name,
        int? PrimaryAuthorId, string? PrimaryAuthorName,
        int Total, int Owned, int Missing, int Percent);

    // Per-series completion ranking for the Series Completion page. Counts each
    // series' books (excluding suppressed foreign/duplicate rows) and how many
    // are owned (a local file or a manual ownership flag). Only series with at
    // least one non-suppressed book are returned. Ordered so the series you're
    // closest to finishing — but haven't — surface first.
    [HttpGet("completion")]
    public async Task<IReadOnlyList<SeriesCompletion>> Completion(CancellationToken ct)
    {
        var rows = await _db.Series.AsNoTracking()
            .Select(s => new
            {
                s.Id, s.Name, s.PrimaryAuthorId,
                PrimaryAuthorName = s.PrimaryAuthor != null ? s.PrimaryAuthor.Name : null,
                Total = s.Books.Count(b => !b.Suppressed),
                Owned = s.Books.Count(b => !b.Suppressed && (b.ManuallyOwned || b.OwnedDifferentEdition || b.LocalFiles.Any())),
            })
            .Where(x => x.Total > 0)
            .ToListAsync(ct);

        return rows
            .Select(x =>
            {
                var missing = x.Total - x.Owned;
                var percent = (int)Math.Round(100.0 * x.Owned / x.Total);
                return new SeriesCompletion(x.Id, x.Name, x.PrimaryAuthorId, x.PrimaryAuthorName,
                    x.Total, x.Owned, missing, percent);
            })
            // Incomplete series first (most-complete at the top), then finished
            // series, each tie broken by fewest missing then name.
            .OrderBy(c => c.Missing == 0)
            .ThenByDescending(c => c.Percent)
            .ThenBy(c => c.Missing)
            .ThenBy(c => c.Name)
            .ToList();
    }

    // One-click "fill the gaps": marks every not-owned, non-suppressed book in
    // the series as Wanted so it shows up on the Wanted page / NZB search.
    [HttpPost("{id:int}/want-missing")]
    public async Task<ActionResult<object>> WantMissing(int id, CancellationToken ct)
    {
        if (!await _db.Series.AnyAsync(s => s.Id == id, ct))
            return NotFound(new { error = "Series not found." });

        var missing = await _db.Books
            .Where(b => b.SeriesId == id && !b.Suppressed && !b.Wanted)
            .Where(BookOwnership.NotOwned)
            .ToListAsync(ct);
        foreach (var b in missing) b.Wanted = true;
        await _db.SaveChangesAsync(ct);
        return Ok(new { updated = missing.Count });
    }

    private static SeriesDetailResponse ToResponse(Series s) => new(
        s.Id, s.Name, s.NormalizedName,
        s.PrimaryAuthorId, s.PrimaryAuthor?.Name,
        s.ParentSeriesId, s.ParentSeries?.Name,
        s.PositionInParent,
        s.SeriesAuthors.Select(sa => new AuthorRef(sa.AuthorId, sa.Author?.Name ?? "")).ToList(),
        s.ChildSeries.OrderBy(c => TryParsePos(c.PositionInParent)).ThenBy(c => c.Name)
            .Select(c => new ChildSeriesRef(c.Id, c.Name, c.PositionInParent)).ToList());

    private static double TryParsePos(string? pos)
        => double.TryParse(pos, System.Globalization.NumberStyles.Any,
               System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : double.MaxValue;
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Controllers;

// User-defined collections (shelves) plus the auto genre tags derived from
// OpenLibrary subjects. Collections are arbitrary cross-author groupings; genre
// tags are read-only facets computed on the fly from Book.Subjects.
[ApiController]
[Route("api/[controller]")]
public class CollectionsController : ControllerBase
{
    private readonly LibraryDbContext _db;
    public CollectionsController(LibraryDbContext db) { _db = db; }

    public sealed record CollectionSummary(int Id, string Name, int BookCount, DateTime CreatedAt);
    public sealed record BookRow(
        int Id, string Title, int? FirstPublishYear, int AuthorId, string AuthorName,
        int? CoverId, bool Owned, bool Wanted, string ReadStatus, string? SeriesName, string? Subjects);
    public sealed record GenreTag(string Genre, int Total, int Owned);
    public sealed record NameRequest(string? Name);
    public sealed record AddBookRequest(int BookId);

    [HttpGet]
    public async Task<IReadOnlyList<CollectionSummary>> List(CancellationToken ct) =>
        await _db.Collections.AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new CollectionSummary(c.Id, c.Name, c.Books.Count, c.CreatedAt))
            .ToListAsync(ct);

    [HttpPost]
    public async Task<ActionResult<CollectionSummary>> Create([FromBody] NameRequest body, CancellationToken ct)
    {
        var name = body.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return BadRequest(new { error = "Name is required." });
        var normalized = TitleNormalizer.Normalize(name);
        if (await _db.Collections.AnyAsync(c => c.NormalizedName == normalized, ct))
            return Conflict(new { error = $"A collection named \"{name}\" already exists." });

        var c = new Collection { Name = name, NormalizedName = normalized, CreatedAt = DateTime.UtcNow };
        _db.Collections.Add(c);
        await _db.SaveChangesAsync(ct);
        return Ok(new CollectionSummary(c.Id, c.Name, 0, c.CreatedAt));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Rename(int id, [FromBody] NameRequest body, CancellationToken ct)
    {
        var c = await _db.Collections.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        var name = body.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return BadRequest(new { error = "Name is required." });
        var normalized = TitleNormalizer.Normalize(name);
        if (await _db.Collections.AnyAsync(x => x.NormalizedName == normalized && x.Id != id, ct))
            return Conflict(new { error = $"A collection named \"{name}\" already exists." });
        c.Name = name; c.NormalizedName = normalized;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var c = await _db.Collections.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        _db.Collections.Remove(c); // cascade removes memberships
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("{id:int}/books")]
    public async Task<ActionResult<IReadOnlyList<BookRow>>> Books(int id, CancellationToken ct)
    {
        if (!await _db.Collections.AnyAsync(c => c.Id == id, ct)) return NotFound();
        var rows = await _db.BookCollections.AsNoTracking()
            .Where(bc => bc.CollectionId == id)
            .OrderByDescending(bc => bc.AddedAt)
            .Select(bc => bc.Book)
            .Select(Project)
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpPost("{id:int}/books")]
    public async Task<IActionResult> AddBook(int id, [FromBody] AddBookRequest body, CancellationToken ct)
    {
        if (!await _db.Collections.AnyAsync(c => c.Id == id, ct)) return NotFound();
        if (!await _db.Books.AnyAsync(b => b.Id == body.BookId, ct))
            return NotFound(new { error = "Book not found." });
        if (!await _db.BookCollections.AnyAsync(bc => bc.CollectionId == id && bc.BookId == body.BookId, ct))
        {
            _db.BookCollections.Add(new BookCollection { CollectionId = id, BookId = body.BookId, AddedAt = DateTime.UtcNow });
            await _db.SaveChangesAsync(ct);
        }
        return NoContent();
    }

    [HttpDelete("{id:int}/books/{bookId:int}")]
    public async Task<IActionResult> RemoveBook(int id, int bookId, CancellationToken ct)
    {
        var bc = await _db.BookCollections.FirstOrDefaultAsync(x => x.CollectionId == id && x.BookId == bookId, ct);
        if (bc is null) return NotFound();
        _db.BookCollections.Remove(bc);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // Which collections a given book belongs to — drives the per-book picker.
    [HttpGet("~/api/books/{bookId:int}/collections")]
    public async Task<IReadOnlyList<int>> ForBook(int bookId, CancellationToken ct) =>
        await _db.BookCollections.AsNoTracking()
            .Where(bc => bc.BookId == bookId)
            .Select(bc => bc.CollectionId)
            .ToListAsync(ct);

    // Auto genre tags from OpenLibrary subjects, with owned/total counts.
    [HttpGet("genres")]
    public async Task<IReadOnlyList<GenreTag>> Genres(CancellationToken ct)
    {
        var rows = await _db.Books.AsNoTracking()
            .Where(b => b.Subjects != null && b.Subjects != "" && !b.Suppressed)
            .Select(b => new { b.Subjects, Owned = b.ManuallyOwned || b.OwnedDifferentEdition || b.LocalFiles.Any() })
            .ToListAsync(ct);

        var total = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var owned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
            foreach (var g in r.Subjects!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                total[g] = total.TryGetValue(g, out var n) ? n + 1 : 1;
                if (r.Owned) owned[g] = owned.TryGetValue(g, out var m) ? m + 1 : 1;
            }

        return total
            .OrderByDescending(kv => kv.Value)
            .Take(60)
            .Select(kv => new GenreTag(kv.Key, kv.Value, owned.TryGetValue(kv.Key, out var o) ? o : 0))
            .ToList();
    }

    // Books carrying a given genre tag (subject). Owned first, then by year.
    [HttpGet("~/api/books/by-genre")]
    public async Task<ActionResult<IReadOnlyList<BookRow>>> ByGenre([FromQuery] string genre, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(genre)) return BadRequest(new { error = "genre is required." });
        var like = $"%{genre.Trim()}%";
        var rows = await _db.Books.AsNoTracking()
            .Where(b => !b.Suppressed && b.Subjects != null && EF.Functions.Like(b.Subjects, like))
            .OrderByDescending(b => b.ManuallyOwned || b.OwnedDifferentEdition || b.LocalFiles.Any())
            .ThenByDescending(b => b.FirstPublishYear)
            .ThenBy(b => b.Title)
            .Take(500)
            .Select(Project)
            .ToListAsync(ct);

        // LIKE %genre% can over-match (substring); keep only exact tag hits.
        var g = genre.Trim();
        return Ok(rows.Where(r => (r.Subjects ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(s => string.Equals(s, g, StringComparison.OrdinalIgnoreCase))).ToList());
    }

    private static System.Linq.Expressions.Expression<Func<Book, BookRow>> Project => b => new BookRow(
        b.Id, b.Title, b.FirstPublishYear, b.AuthorId, b.Author.Name, b.CoverId,
        b.ManuallyOwned || b.OwnedDifferentEdition || b.LocalFiles.Any(), b.Wanted, b.ReadStatus.ToString(),
        b.Series != null ? b.Series.Name : null, b.Subjects);
}

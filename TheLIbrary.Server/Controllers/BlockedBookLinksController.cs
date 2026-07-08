using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;

namespace TheLibrary.Server.Controllers;

// Manages the permanent "never re-link this exact file to this exact book" records
// created by the Duplicates page's "Unlink" action (see LinkBlocklist). Every
// automated matching path and apply/bulk-apply flow checks this before setting
// BookId; removing a row here is the only way to lift a block.
[ApiController]
[Route("api/blocked-book-links")]
public class BlockedBookLinksController : ControllerBase
{
    private readonly LibraryDbContext _db;
    public BlockedBookLinksController(LibraryDbContext db) { _db = db; }

    public sealed record BlockedLinkDto(
        int Id, string FullPath, int BookId, string? BookTitle, string? AuthorName, DateTime BlockedAt);

    [HttpGet]
    public async Task<IReadOnlyList<BlockedLinkDto>> List(CancellationToken ct)
    {
        return await _db.BlockedBookLinks.AsNoTracking()
            .OrderByDescending(x => x.BlockedAt)
            .Select(x => new BlockedLinkDto(
                x.Id, x.FullPath, x.BookId,
                _db.Books.Where(b => b.Id == x.BookId).Select(b => b.Title).FirstOrDefault(),
                _db.Books.Where(b => b.Id == x.BookId).Select(b => b.Author!.Name).FirstOrDefault(),
                x.BlockedAt))
            .ToListAsync(ct);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Remove(int id, CancellationToken ct)
    {
        var row = await _db.BlockedBookLinks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return NotFound();
        _db.BlockedBookLinks.Remove(row);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;

namespace TheLibrary.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StatsController : ControllerBase
{
    private readonly LibraryDbContext _db;
    public StatsController(LibraryDbContext db) { _db = db; }

    [HttpGet]
    public async Task<LibraryStats> Get(CancellationToken ct)
    {
        var totalBooks = await _db.Books.CountAsync(ct);
        var ownedBooks = await _db.Books.CountAsync(
            b => b.ManuallyOwned || b.LocalFiles.Any(), ct);
        var missingBooks = totalBooks - ownedBooks;

        var readCount = await _db.Books.CountAsync(b => b.ReadStatus == ReadStatus.Read, ct);
        var readingCount = await _db.Books.CountAsync(b => b.ReadStatus == ReadStatus.Reading, ct);
        var wantedCount = await _db.Books.CountAsync(b => b.Wanted && !b.ManuallyOwned && !b.LocalFiles.Any(), ct);

        var totalAuthors = await _db.Authors.CountAsync(ct);
        var activeAuthors = await _db.Authors.CountAsync(a => a.Status == AuthorStatus.Active, ct);
        var starredAuthors = await _db.Authors.CountAsync(a => a.Priority >= 1, ct);

        // Books read per year (last 10 years).
        var readByYear = await _db.Books
            .AsNoTracking()
            .Where(b => b.ReadStatus == ReadStatus.Read && b.ReadAt != null)
            .GroupBy(b => b.ReadAt!.Value.Year)
            .Select(g => new { Year = g.Key, Count = g.Count() })
            .OrderBy(r => r.Year)
            .ToListAsync(ct);

        // Top 20 genres by book count (owned books only).
        var subjectRows = await _db.Books.AsNoTracking()
            .Where(b => b.Subjects != null && b.Subjects != "" && (b.ManuallyOwned || b.LocalFiles.Any()))
            .Select(b => b.Subjects!)
            .ToListAsync(ct);

        var genreCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in subjectRows)
            foreach (var tag in row.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                genreCounts[tag] = genreCounts.TryGetValue(tag, out var n) ? n + 1 : 1;

        var topGenres = genreCounts
            .OrderByDescending(kv => kv.Value)
            .Take(20)
            .Select(kv => new GenreCount(kv.Key, kv.Value))
            .ToList();

        // Author coverage: % of books owned, for starred authors.
        var coverageRaw = await _db.Authors.AsNoTracking()
            .Where(a => a.Priority >= 1 && a.Books.Any())
            .Select(a => new
            {
                a.Id, a.Name, a.Priority,
                Total = a.Books.Count(),
                Owned = a.Books.Count(b => b.ManuallyOwned || b.LocalFiles.Any())
            })
            .OrderByDescending(a => a.Priority)
            .ThenBy(a => a.Name)
            .ToListAsync(ct);

        var coverage = coverageRaw.Select(r => new AuthorCoverage(
            r.Id, r.Name, r.Priority, r.Total, r.Owned,
            r.Total == 0 ? 0 : (int)Math.Round(100.0 * r.Owned / r.Total)
        )).ToList();

        return new LibraryStats(
            totalBooks, ownedBooks, missingBooks,
            readCount, readingCount, wantedCount,
            totalAuthors, activeAuthors, starredAuthors,
            readByYear.Select(r => new YearCount(r.Year, r.Count)).ToList(),
            topGenres,
            coverage);
    }

    public sealed record LibraryStats(
        int TotalBooks,
        int OwnedBooks,
        int MissingBooks,
        int ReadBooks,
        int ReadingBooks,
        int WantedBooks,
        int TotalAuthors,
        int ActiveAuthors,
        int StarredAuthors,
        IReadOnlyList<YearCount> ReadByYear,
        IReadOnlyList<GenreCount> TopGenres,
        IReadOnlyList<AuthorCoverage> AuthorCoverage);

    public sealed record YearCount(int Year, int Count);
    public sealed record GenreCount(string Genre, int Count);
    public sealed record AuthorCoverage(int Id, string Name, int Priority, int Total, int Owned, int Percent);
}

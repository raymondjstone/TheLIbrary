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
        var ownedBooks = await _db.Books.CountAsync(BookOwnership.Owned, ct);
        var missingBooks = totalBooks - ownedBooks;

        var readCount = await _db.Books.CountAsync(b => b.ReadStatus == ReadStatus.Read, ct);
        var readingCount = await _db.Books.CountAsync(b => b.ReadStatus == ReadStatus.Reading, ct);
        var wantedCount = await _db.Books.CountAsync(b => b.Wanted && !b.ManuallyOwned && !b.OwnedDifferentEdition && !b.LocalFiles.Any(), ct);

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
            .Where(b => b.Subjects != null && b.Subjects != "").Where(BookOwnership.Owned)
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
                Owned = a.Books.Count(b => b.ManuallyOwned || b.OwnedDifferentEdition || b.LocalFiles.Any())
            })
            .OrderByDescending(a => a.Priority)
            .ThenBy(a => a.Name)
            .ToListAsync(ct);

        var coverage = coverageRaw.Select(r => new AuthorCoverage(
            r.Id, r.Name, r.Priority, r.Total, r.Owned,
            r.Total == 0 ? 0 : (int)Math.Round(100.0 * r.Owned / r.Total)
        )).ToList();

        // Format breakdown: count of local files by extension (e.g. epub, pdf, mobi).
        var formatRows = await _db.LocalBookFiles.AsNoTracking()
            .Select(f => f.FullPath)
            .ToListAsync(ct);
        var formatCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in formatRows)
        {
            var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            if (!string.IsNullOrEmpty(ext))
                formatCounts[ext] = formatCounts.TryGetValue(ext, out var n) ? n + 1 : 1;
        }
        var formatBreakdown = formatCounts
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new FormatCount(kv.Key, kv.Value))
            .ToList();

        // Acquisition rate: files added per calendar month over the last 24 months.
        var cutoff = DateTime.UtcNow.AddMonths(-24);
        var acquisitionRaw = await _db.LocalBookFiles.AsNoTracking()
            .Where(f => f.ModifiedAt >= cutoff)
            .Select(f => new { f.ModifiedAt })
            .ToListAsync(ct);
        var acquisitionCounts = acquisitionRaw
            .GroupBy(f => new { f.ModifiedAt.Year, f.ModifiedAt.Month })
            .Select(g => new MonthCount($"{g.Key.Year}-{g.Key.Month:D2}", g.Count()))
            .OrderBy(m => m.Month)
            .ToList();

        return new LibraryStats(
            totalBooks, ownedBooks, missingBooks,
            readCount, readingCount, wantedCount,
            totalAuthors, activeAuthors, starredAuthors,
            readByYear.Select(r => new YearCount(r.Year, r.Count)).ToList(),
            topGenres,
            coverage,
            formatBreakdown,
            acquisitionCounts);
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
        IReadOnlyList<AuthorCoverage> AuthorCoverage,
        IReadOnlyList<FormatCount> FormatBreakdown,
        IReadOnlyList<MonthCount> AcquisitionByMonth);

    public sealed record YearCount(int Year, int Count);
    public sealed record GenreCount(string Genre, int Count);
    public sealed record AuthorCoverage(int Id, string Name, int Priority, int Total, int Owned, int Percent);
    public sealed record FormatCount(string Format, int Count);
    public sealed record MonthCount(string Month, int Count);
}

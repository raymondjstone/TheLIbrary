using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;

namespace TheLibrary.Server.Controllers;

// "Authors you might want to watch." Derives a taste profile from the genres of
// the books you own, then surfaces authors already in your catalogue that you
// HAVEN'T starred yet whose work overlaps that profile — plus co-authors who
// appear on series you own. Uses only local data (no OpenLibrary calls).
[ApiController]
[Route("api/[controller]")]
public class RecommendationsController : ControllerBase
{
    private readonly LibraryDbContext _db;
    public RecommendationsController(LibraryDbContext db) { _db = db; }

    public sealed record AuthorSuggestion(
        int Id, string Name, int Priority, string Status,
        int Score, int BookCount, IReadOnlyList<string> Reasons, IReadOnlyList<string> Genres);

    private static IEnumerable<string> SplitSubjects(string? s) =>
        string.IsNullOrWhiteSpace(s)
            ? Array.Empty<string>()
            : s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [HttpGet]
    public async Task<IReadOnlyList<AuthorSuggestion>> Get(CancellationToken ct)
    {
        // 1. Taste profile: top genres weighted by how many OWNED books carry them.
        var ownedSubjects = await _db.Books.AsNoTracking()
            .Where(b => (b.ManuallyOwned || b.OwnedDifferentEdition || b.LocalFiles.Any()) && b.Subjects != null && b.Subjects != "")
            .Select(b => b.Subjects!)
            .ToListAsync(ct);

        var tasteWeights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in ownedSubjects)
            foreach (var subj in SplitSubjects(row))
                tasteWeights[subj] = tasteWeights.TryGetValue(subj, out var n) ? n + 1 : 1;

        var topGenres = tasteWeights
            .OrderByDescending(kv => kv.Value)
            .Take(12)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (topGenres.Count == 0) return Array.Empty<AuthorSuggestion>();

        // 2. Candidate authors: in the catalogue, not starred (Priority 0), active
        //    or pending, not a linked duplicate. Pull (author, subjects) for their
        //    books and score subject overlap with the taste profile in memory.
        // Bounded sample — scoring is a heuristic, so a large slice adds cost
        // (this can sit over a multi-million-row Books table) without improving
        // the top suggestions.
        var candidateBooks = await _db.Books.AsNoTracking()
            .Where(b => b.Author.Priority == 0
                     && b.Author.LinkedToAuthorId == null
                     && !b.Author.RecommendationRejected
                     && (b.Author.Status == AuthorStatus.Active || b.Author.Status == AuthorStatus.Pending)
                     && b.Subjects != null && b.Subjects != "")
            .Select(b => new { b.AuthorId, b.Author.Name, b.Author.Status, b.Subjects })
            .Take(40_000)
            .ToListAsync(ct);

        var byAuthor = new Dictionary<int, (string Name, AuthorStatus Status, int Books, Dictionary<string, int> Hits)>();
        foreach (var row in candidateBooks)
        {
            if (!byAuthor.TryGetValue(row.AuthorId, out var agg))
                agg = (row.Name, row.Status, 0, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
            agg.Books++;
            foreach (var subj in SplitSubjects(row.Subjects))
                if (topGenres.Contains(subj))
                    agg.Hits[subj] = agg.Hits.TryGetValue(subj, out var n) ? n + 1 : 1;
            byAuthor[row.AuthorId] = agg;
        }

        // 3. Co-authorship signal: authors who share a series with one you own.
        var coAuthorIds = await _db.SeriesAuthors.AsNoTracking()
            .Where(sa => sa.Author.Priority == 0
                      && sa.Author.LinkedToAuthorId == null
                      && !sa.Author.RecommendationRejected
                      && sa.Series.Books.Any(b => b.ManuallyOwned || b.OwnedDifferentEdition || b.LocalFiles.Any()))
            .Select(sa => sa.AuthorId)
            .Distinct()
            .ToListAsync(ct);
        var coAuthorSet = coAuthorIds.ToHashSet();

        var suggestions = byAuthor
            .Where(kv => kv.Value.Hits.Count > 0 || coAuthorSet.Contains(kv.Key))
            .Select(kv =>
            {
                var (name, status, books, hits) = kv.Value;
                // Score: total genre-match hits + a flat bonus for shared-series.
                var score = hits.Values.Sum() + (coAuthorSet.Contains(kv.Key) ? 5 : 0);
                var genres = hits.OrderByDescending(h => h.Value).Take(4).Select(h => h.Key).ToList();
                var reasons = new List<string>();
                if (genres.Count > 0) reasons.Add($"Writes {string.Join(", ", genres)} — among your top genres");
                if (coAuthorSet.Contains(kv.Key)) reasons.Add("Co-author on a series you own");
                return new AuthorSuggestion(kv.Key, name, 0, status.ToString(), score, books, reasons, genres);
            })
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.BookCount)
            .ThenBy(s => s.Name)
            .Take(40)
            .ToList();

        return suggestions;
    }

    // Dismiss an author from recommendations for good. Independent of starring —
    // a declined suggestion must not return when the taste profile is recomputed.
    [HttpPost("{id:int}/reject")]
    public async Task<IActionResult> Reject(int id, CancellationToken ct)
    {
        var author = await _db.Authors.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (author is null) return NotFound(new { error = "Author not found." });
        if (!author.RecommendationRejected)
        {
            author.RecommendationRejected = true;
            await _db.SaveChangesAsync(ct);
        }
        return NoContent();
    }

    // Undo a rejection (e.g. a mis-click) so the author can be suggested again.
    [HttpDelete("{id:int}/reject")]
    public async Task<IActionResult> UnReject(int id, CancellationToken ct)
    {
        var author = await _db.Authors.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (author is null) return NotFound(new { error = "Author not found." });
        if (author.RecommendationRejected)
        {
            author.RecommendationRejected = false;
            await _db.SaveChangesAsync(ct);
        }
        return NoContent();
    }
}

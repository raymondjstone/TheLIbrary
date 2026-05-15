using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ImportController : ControllerBase
{
    private readonly LibraryDbContext _db;
    public ImportController(LibraryDbContext db) { _db = db; }

    public sealed record GoodreadsImportResult(
        int Matched,
        int AlreadyRead,
        int Unmatched,
        IReadOnlyList<string> UnmatchedTitles);

    // Accepts a Goodreads export CSV. Matches rows on normalized title + author
    // and sets ReadStatus = Read (or Reading / Unread for other shelves) and
    // ReadAt from the "Date Read" column. Rows with Exclusive Shelf = "to-read"
    // set Wanted = true on missing books.
    [HttpPost("goodreads")]
    [RequestSizeLimit(10 * 1024 * 1024)] // 10 MB
    public async Task<ActionResult<GoodreadsImportResult>> Goodreads(
        IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "No file uploaded" });

        List<GoodreadsRow> rows;
        try
        {
            using var reader = new StreamReader(file.OpenReadStream());
            rows = ParseGoodreadsCsv(await reader.ReadToEndAsync(ct));
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = $"Failed to parse CSV: {ex.Message}" });
        }

        // Load all books with their authors into memory for matching.
        var allBooks = await _db.Books.AsNoTracking()
            .Select(b => new { b.Id, b.NormalizedTitle, AuthorNorm = b.Author.Name })
            .ToListAsync(ct);

        // Build lookup: normalized-title -> list of book ids
        var byTitle = allBooks
            .Where(b => b.NormalizedTitle != null)
            .GroupBy(b => b.NormalizedTitle!)
            .ToDictionary(g => g.Key, g => g.Select(b => b.Id).ToList());

        int matched = 0, alreadyRead = 0, unmatched = 0;
        var unmatchedTitles = new List<string>();
        var toUpdate = new Dictionary<int, (ReadStatus Status, DateTime? ReadAt, bool Wanted)>();

        foreach (var row in rows)
        {
            var normTitle = TitleNormalizer.Normalize(row.Title);
            if (normTitle is null) continue;

            if (!byTitle.TryGetValue(normTitle, out var candidateIds))
            {
                unmatched++;
                unmatchedTitles.Add(row.Title);
                continue;
            }

            // If multiple books share the same normalized title, pick the best
            // match by also checking author (normalized). Fall back to first.
            var normAuthor = TitleNormalizer.NormalizeAuthor(row.Author);
            var allCandidates = allBooks.Where(b => candidateIds.Contains(b.Id)).ToList();
            var best = allCandidates.FirstOrDefault(b =>
                TitleNormalizer.NormalizeAuthor(b.AuthorNorm) == normAuthor)
                ?? allCandidates[0];

            var readStatus = row.Shelf switch
            {
                "read" => ReadStatus.Read,
                "currently-reading" => ReadStatus.Reading,
                _ => ReadStatus.Unread
            };

            if (toUpdate.TryGetValue(best.Id, out var existing) && existing.Status == ReadStatus.Read)
            {
                alreadyRead++;
                continue;
            }

            var wanted = row.Shelf == "to-read";
            toUpdate[best.Id] = (readStatus, row.DateRead, wanted);
            matched++;
        }

        // Batch update in chunks to avoid huge SQL statements.
        var readIds = toUpdate.Where(kv => kv.Value.Status == ReadStatus.Read).Select(kv => kv.Key).ToList();
        var readingIds = toUpdate.Where(kv => kv.Value.Status == ReadStatus.Reading).Select(kv => kv.Key).ToList();
        var wantedIds = toUpdate.Where(kv => kv.Value.Wanted).Select(kv => kv.Key).ToList();

        // For "read" rows we need to set ReadAt per-row — do individually for those with a date.
        var readWithDate = toUpdate.Where(kv => kv.Value.Status == ReadStatus.Read && kv.Value.ReadAt.HasValue).ToList();
        var readWithoutDate = readIds.Except(readWithDate.Select(kv => kv.Key)).ToList();

        if (readWithoutDate.Count > 0)
        {
            await _db.Books.Where(b => readWithoutDate.Contains(b.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.ReadStatus, _ => ReadStatus.Read)
                    .SetProperty(b => b.ReadAt, _ => (DateTime?)null), ct);
        }

        foreach (var (id, (_, readAt, _)) in readWithDate)
        {
            var book = await _db.Books.FirstOrDefaultAsync(b => b.Id == id, ct);
            if (book is null) continue;
            book.ReadStatus = ReadStatus.Read;
            book.ReadAt = readAt;
        }

        if (readingIds.Count > 0)
            await _db.Books.Where(b => readingIds.Contains(b.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.ReadStatus, _ => ReadStatus.Reading), ct);

        if (wantedIds.Count > 0)
            await _db.Books.Where(b => wantedIds.Contains(b.Id) && !b.ManuallyOwned && !b.LocalFiles.Any())
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.Wanted, _ => true), ct);

        await _db.SaveChangesAsync(ct);

        return Ok(new GoodreadsImportResult(
            matched, alreadyRead, unmatched,
            unmatchedTitles.Take(50).ToList()));
    }

    private sealed record GoodreadsRow(
        string Title, string Author, string Shelf, DateTime? DateRead, int? Rating);

    private static List<GoodreadsRow> ParseGoodreadsCsv(string csv)
    {
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return new();

        // Parse header to find column indices robustly.
        var header = SplitCsvLine(lines[0]);
        int Idx(string name) => Array.FindIndex(header, h => h.Equals(name, StringComparison.OrdinalIgnoreCase));

        var titleIdx = Idx("Title");
        var authorIdx = Idx("Author");
        var shelfIdx = Idx("Exclusive Shelf");
        var dateReadIdx = Idx("Date Read");
        var ratingIdx = Idx("My Rating");

        if (titleIdx < 0 || authorIdx < 0) return new();

        var rows = new List<GoodreadsRow>();
        for (var i = 1; i < lines.Length; i++)
        {
            var cols = SplitCsvLine(lines[i]);
            if (cols.Length <= Math.Max(titleIdx, authorIdx)) continue;

            var title = cols[titleIdx].Trim('"', ' ');
            var author = authorIdx < cols.Length ? cols[authorIdx].Trim('"', ' ') : "";
            var shelf = shelfIdx >= 0 && shelfIdx < cols.Length ? cols[shelfIdx].Trim('"', ' ') : "";
            var dateStr = dateReadIdx >= 0 && dateReadIdx < cols.Length ? cols[dateReadIdx].Trim('"', ' ') : "";
            var ratingStr = ratingIdx >= 0 && ratingIdx < cols.Length ? cols[ratingIdx].Trim('"', ' ') : "";

            DateTime? dateRead = DateTime.TryParse(dateStr, out var d) ? d : null;
            int? rating = int.TryParse(ratingStr, out var r) ? r : null;

            if (!string.IsNullOrWhiteSpace(title))
                rows.Add(new GoodreadsRow(title, author, shelf, dateRead, rating));
        }

        return rows;
    }

    // Minimal CSV line splitter that handles quoted fields (including commas inside quotes).
    private static string[] SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                { sb.Append('"'); i++; }
                else
                    inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }
            else
                sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields.ToArray();
    }
}

using System.Text;
using System.Text.RegularExpressions;
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

    // Pre-replace "&" with "and" so "Rock & Roll" and "Rock and Roll" produce
    // the same loose key. Collapsing & on either side handles "A&B", "A &B",
    // and "A & B" uniformly.
    private static readonly Regex AmpersandRx =
        new(@"\s*&\s*", RegexOptions.Compiled);

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
            .Where(b => !string.IsNullOrEmpty(b.NormalizedTitle))
            .GroupBy(b => b.NormalizedTitle!)
            .ToDictionary(g => g.Key, g => g.Select(b => b.Id).ToList());

        int matched = 0, alreadyRead = 0, unmatched = 0;
        var unmatchedTitles = new List<string>();
        var toUpdate = new Dictionary<int, (ReadStatus Status, DateTime? ReadAt, bool Wanted)>();

        foreach (var row in rows)
        {
            var normTitle = TitleNormalizer.Normalize(row.Title);
            if (string.IsNullOrEmpty(normTitle)) continue;

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

    public sealed record PhysicalBooksImportResult(
        int Matched,
        int AlreadyOwned,
        int Skipped,
        IReadOnlyList<string> Unmatched);

    // Accepts a fixed-width plain-text file with three columns:
    //   Author (26 chars)  Title (44 chars)  Series+position (rest)
    // Also accepts tab-separated rows. Rows with an empty title are skipped.
    // Matching is by normalised title + author; books that match are marked
    // ManuallyOwned = true. Already-owned books are counted but not updated.
    [HttpPost("physical-books")]
    [RequestSizeLimit(5 * 1024 * 1024)]
    public async Task<ActionResult<PhysicalBooksImportResult>> PhysicalBooks(
        IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "No file uploaded" });

        string text;
        try
        {
            using var reader = new StreamReader(file.OpenReadStream());
            text = await reader.ReadToEndAsync(ct);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = $"Failed to read file: {ex.Message}" });
        }

        var rows = ParsePhysicalBooksFile(text);
        var index = await PhysicalMatchIndex.LoadAsync(_db, ct);

        int matched = 0, alreadyOwned = 0, skipped = 0;
        var unmatchedRows = new List<PhysicalBookRow>();
        var toMarkOwned = new HashSet<int>();
        var seen = new HashSet<int>();

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Title)) { skipped++; continue; }

            var hit = index.TryMatch(row.Title, row.Author);
            if (hit is null) { unmatchedRows.Add(row); continue; }

            if (!seen.Add(hit.Value.Id)) continue;

            if (hit.Value.ManuallyOwned) { alreadyOwned++; continue; }

            toMarkOwned.Add(hit.Value.Id);
            matched++;
        }

        if (toMarkOwned.Count > 0)
            await MarkOwnedAsync(toMarkOwned, ct);

        await PersistUnmatchedAsync(unmatchedRows, ct);

        // Display string for each unmatched row — built once from the source row.
        var unmatchedDisplay = unmatchedRows
            .Select(FormatUnmatched)
            .ToList();

        return Ok(new PhysicalBooksImportResult(
            matched, alreadyOwned, skipped, unmatchedDisplay));
    }

    // GET /api/import/physical-books/unmatched
    [HttpGet("physical-books/unmatched")]
    public async Task<IActionResult> GetUnmatched(CancellationToken ct)
    {
        var rows = await _db.PhysicalBookUnmatched
            .OrderBy(u => u.Author).ThenBy(u => u.Title)
            .Select(u => new { u.Id, u.Author, u.Title, u.SeriesPos, u.AddedAt })
            .ToListAsync(ct);
        return Ok(rows);
    }

    public sealed record UpdateUnmatchedRequest(string Author, string Title, string SeriesPos);

    // PUT /api/import/physical-books/unmatched/{id}
    [HttpPut("physical-books/unmatched/{id:int}")]
    public async Task<IActionResult> UpdateUnmatched(int id, [FromBody] UpdateUnmatchedRequest req, CancellationToken ct)
    {
        var row = await _db.PhysicalBookUnmatched.FindAsync([id], ct);
        if (row is null) return NotFound();
        row.Author    = req.Author ?? "";
        row.Title     = req.Title ?? "";
        row.SeriesPos = req.SeriesPos ?? "";
        await _db.SaveChangesAsync(ct);
        return Ok(new { row.Id, row.Author, row.Title, row.SeriesPos, row.AddedAt });
    }

    // DELETE /api/import/physical-books/unmatched/{id}
    [HttpDelete("physical-books/unmatched/{id:int}")]
    public async Task<IActionResult> DeleteUnmatched(int id, CancellationToken ct)
    {
        await _db.PhysicalBookUnmatched.Where(u => u.Id == id).ExecuteDeleteAsync(ct);
        return NoContent();
    }

    public sealed record RematchResult(int Matched, int StillUnmatched);

    // POST /api/import/physical-books/unmatched/rematch
    [HttpPost("physical-books/unmatched/rematch")]
    public async Task<ActionResult<RematchResult>> Rematch(CancellationToken ct)
    {
        var pending = await _db.PhysicalBookUnmatched.ToListAsync(ct);
        if (pending.Count == 0) return Ok(new RematchResult(0, 0));

        var index = await PhysicalMatchIndex.LoadAsync(_db, ct);

        var toMarkOwned = new HashSet<int>();
        var nowMatched = new List<int>();

        foreach (var row in pending)
        {
            var hit = index.TryMatch(row.Title, row.Author);
            if (hit is null) continue;
            if (!hit.Value.ManuallyOwned)
                toMarkOwned.Add(hit.Value.Id);
            nowMatched.Add(row.Id);
        }

        if (toMarkOwned.Count > 0)
            await MarkOwnedAsync(toMarkOwned, ct);

        if (nowMatched.Count > 0)
            await _db.PhysicalBookUnmatched
                .Where(u => nowMatched.Contains(u.Id))
                .ExecuteDeleteAsync(ct);

        return Ok(new RematchResult(nowMatched.Count, pending.Count - nowMatched.Count));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // Marks a set of books as ManuallyOwned = true with the current timestamp.
    private Task MarkOwnedAsync(IEnumerable<int> bookIds, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var ids = bookIds.ToList();
        return _db.Books
            .Where(b => ids.Contains(b.Id))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.ManuallyOwned, _ => true)
                .SetProperty(b => b.ManuallyOwnedAt, _ => now), ct);
    }

    // Adds the given rows to PhysicalBookUnmatched, skipping any row whose
    // (author, title) already exists case-insensitively.
    private async Task PersistUnmatchedAsync(IReadOnlyList<PhysicalBookRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        var existingKeys = await _db.PhysicalBookUnmatched
            .Select(u => new { u.Author, u.Title })
            .ToListAsync(ct);
        var keys = existingKeys
            .Select(u => (u.Author.Trim().ToLowerInvariant(), u.Title.Trim().ToLowerInvariant()))
            .ToHashSet();

        var addedAt = DateTime.UtcNow;
        foreach (var row in rows)
        {
            var key = (row.Author.Trim().ToLowerInvariant(), row.Title.Trim().ToLowerInvariant());
            if (!keys.Add(key)) continue;

            _db.PhysicalBookUnmatched.Add(new Data.Models.PhysicalBookUnmatched
            {
                Author    = row.Author,
                Title     = row.Title,
                SeriesPos = row.SeriesPos,
                AddedAt   = addedAt,
            });
        }
        await _db.SaveChangesAsync(ct);
    }

    private static string FormatUnmatched(PhysicalBookRow row) =>
        string.IsNullOrWhiteSpace(row.Author) ? row.Title : $"{row.Author} — {row.Title}";

    // Loose key for physical-books matching: pre-replace & → and, then standard
    // normalize, then collapse all spaces. Handles hyphens vs spaces, apostrophe
    // possessives, ampersand vs "and", and general punctuation differences.
    private static string PhysicalLooseKey(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var s = AmpersandRx.Replace(title, " and ");
        return TitleNormalizer.Normalize(s).Replace(" ", "");
    }

    private sealed record PhysicalBookRow(string Author, string Title, string SeriesPos);

    // In-memory index of every book in the library, keyed by both normalized
    // title and a looser "no-spaces, & = and" key, with one TryMatch entry
    // point shared by the initial import and the unmatched-table rematch.
    private sealed class PhysicalMatchIndex
    {
        public readonly record struct BookEntry(int Id, bool ManuallyOwned, string NormAuthor);

        private readonly Dictionary<string, List<BookEntry>> _byTitle;
        private readonly Dictionary<string, List<BookEntry>> _byLoose;

        private PhysicalMatchIndex(
            Dictionary<string, List<BookEntry>> byTitle,
            Dictionary<string, List<BookEntry>> byLoose)
        {
            _byTitle = byTitle;
            _byLoose = byLoose;
        }

        public static async Task<PhysicalMatchIndex> LoadAsync(LibraryDbContext db, CancellationToken ct)
        {
            var raw = await db.Books.AsNoTracking()
                .Select(b => new { b.Id, b.Title, b.NormalizedTitle, b.ManuallyOwned, AuthorName = b.Author.Name })
                .ToListAsync(ct);

            var byTitle = new Dictionary<string, List<BookEntry>>();
            var byLoose = new Dictionary<string, List<BookEntry>>();

            foreach (var b in raw)
            {
                if (string.IsNullOrEmpty(b.NormalizedTitle)) continue;
                var entry = new BookEntry(b.Id, b.ManuallyOwned,
                    TitleNormalizer.NormalizeAuthor(b.AuthorName));

                Add(byTitle, b.NormalizedTitle!, entry);
                var loose = PhysicalLooseKey(b.Title);
                if (loose.Length > 0) Add(byLoose, loose, entry);
            }
            return new PhysicalMatchIndex(byTitle, byLoose);

            static void Add(Dictionary<string, List<BookEntry>> map, string key, BookEntry entry)
            {
                if (!map.TryGetValue(key, out var list))
                    map[key] = list = new List<BookEntry>();
                list.Add(entry);
            }
        }

        // Returns the best matching book for a (title, author) pair, or null when
        // neither the standard nor the loose-key lookup finds anything.
        public BookEntry? TryMatch(string? rawTitle, string? rawAuthor)
        {
            var normTitle = TitleNormalizer.Normalize(rawTitle);
            if (normTitle.Length == 0) return null;

            if (!_byTitle.TryGetValue(normTitle, out var candidates))
            {
                var loose = PhysicalLooseKey(rawTitle);
                if (loose.Length == 0 || !_byLoose.TryGetValue(loose, out candidates))
                    return null;
            }

            var normAuthor = TitleNormalizer.NormalizeAuthor(rawAuthor);
            return candidates.FirstOrDefault(c => c.NormAuthor == normAuthor) is { Id: > 0 } match
                ? match
                : candidates[0];
        }
    }

    // Parses the fixed-width layout: author (26 chars), title (44 chars), series (rest).
    // Falls back to tab-separated columns when the line contains a tab character.
    private static List<PhysicalBookRow> ParsePhysicalBooksFile(string text)
    {
        var rows = new List<PhysicalBookRow>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;

            string author, title, seriesPos;
            if (line.Contains('\t'))
            {
                var parts = line.Split('\t');
                author    = parts.Length > 0 ? parts[0].Trim() : "";
                title     = parts.Length > 1 ? parts[1].Trim() : "";
                seriesPos = parts.Length > 2 ? parts[2].Trim() : "";
            }
            else
            {
                var padded = line.TrimEnd();
                if (padded.Length >= 70)
                {
                    author    = padded[..26].Trim();
                    title     = padded[26..70].Trim();
                    seriesPos = padded[70..].Trim();
                }
                else if (padded.Length >= 26)
                {
                    author    = padded[..26].Trim();
                    title     = padded[26..].Trim();
                    seriesPos = "";
                }
                else
                {
                    author    = padded.Trim();
                    title     = "";
                    seriesPos = "";
                }
            }

            if (string.IsNullOrWhiteSpace(author)) continue;
            rows.Add(new PhysicalBookRow(author, title, seriesPos));
        }
        return rows;
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
        var sb = new StringBuilder();
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

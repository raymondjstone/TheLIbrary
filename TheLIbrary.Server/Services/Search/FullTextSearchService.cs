using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;

namespace TheLibrary.Server.Services.Search;

// Optional full-text search over the readable text of matched ebooks. Strictly
// opt-in (AppSettings["FullTextSearchEnabled"], default OFF): extracting and
// storing book text is heavy, so nothing is indexed until the user turns it on
// from Settings. Indexing is incremental and capped; search is a substring
// query over the extracted text plus title/author.
public class FullTextSearchService
{
    public const int MaxCharsPerBook = 200_000;   // ~400 KB of text per book, capped
    public const int DefaultBatch = 200;          // books indexed per reindex call

    private static int _running;                  // 0/1 guard against overlapping runs

    private readonly LibraryDbContext _db;
    private readonly BookTextReader _reader;
    private readonly ILogger<FullTextSearchService> _log;

    public FullTextSearchService(LibraryDbContext db, BookTextReader reader, ILogger<FullTextSearchService> log)
    {
        _db = db; _reader = reader; _log = log;
    }

    public async Task<bool> IsEnabledAsync(CancellationToken ct)
    {
        var v = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.FullTextSearchEnabled)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    public sealed record IndexStatus(bool Enabled, int Indexed, int Eligible, bool Running, DateTime? LastIndexedAt);

    public async Task<IndexStatus> StatusAsync(CancellationToken ct)
    {
        var enabled = await IsEnabledAsync(ct);
        var indexed = await _db.BookTextIndexes.CountAsync(ct);
        var eligible = await _db.LocalBookFiles.AsNoTracking()
            .Where(f => f.BookId != null)
            .Select(f => f.BookId)
            .Distinct()
            .CountAsync(ct);
        var last = await _db.BookTextIndexes.AsNoTracking()
            .OrderByDescending(x => x.IndexedAt).Select(x => (DateTime?)x.IndexedAt).FirstOrDefaultAsync(ct);
        return new IndexStatus(enabled, indexed, eligible, _running == 1, last);
    }

    public sealed record IndexResult(bool Enabled, int Indexed, int Remaining);

    // Indexes up to `max` not-yet-indexed books. Returns how many were indexed
    // this call and a rough count still outstanding, so a UI can loop to completion.
    public async Task<IndexResult> IndexBatchAsync(int max, CancellationToken ct)
    {
        if (!await IsEnabledAsync(ct)) return new IndexResult(false, 0, 0);
        if (Interlocked.CompareExchange(ref _running, 1, 0) == 1)
            return new IndexResult(true, 0, await OutstandingAsync(ct));
        try
        {
            // Candidate local files whose book isn't indexed yet. Over-fetch a bit
            // since non-ebook / directory rows get filtered out in memory.
            var candidates = await _db.LocalBookFiles.AsNoTracking()
                .Where(f => f.BookId != null && !_db.BookTextIndexes.Any(ix => ix.BookId == f.BookId))
                .Select(f => new { BookId = f.BookId!.Value, f.FullPath, f.AuthorId, f.SizeBytes, f.ModifiedAt })
                .Take(max * 4)
                .ToListAsync(ct);

            var byBook = candidates
                .Where(c => BookIntegrityChecker.IsEbook(c.FullPath) && File.Exists(c.FullPath))
                .GroupBy(c => c.BookId)
                .Take(max)
                .ToList();

            var indexed = 0;
            foreach (var grp in byBook)
            {
                ct.ThrowIfCancellationRequested();
                var c = grp.First();
                string content;
                try
                {
                    content = await _reader.ReadHeadAsync(c.FullPath, MaxCharsPerBook, ct) ?? "";
                }
                catch (Exception ex)
                {
                    // Store an empty row so an unreadable book isn't retried forever.
                    _log.LogDebug(ex, "Full-text index: failed to read {Path}", c.FullPath);
                    content = "";
                }

                _db.BookTextIndexes.Add(new BookTextIndex
                {
                    BookId = c.BookId,
                    AuthorId = c.AuthorId,
                    FullPath = c.FullPath,
                    Content = content,
                    SizeBytes = c.SizeBytes,
                    ModifiedAt = c.ModifiedAt,
                    IndexedAt = DateTime.UtcNow,
                });
                indexed++;
                // Save periodically to keep the transaction small.
                if (indexed % 25 == 0) await _db.SaveChangesAsync(ct);
            }
            await _db.SaveChangesAsync(ct);
            return new IndexResult(true, indexed, await OutstandingAsync(ct));
        }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    private Task<int> OutstandingAsync(CancellationToken ct) =>
        _db.LocalBookFiles.AsNoTracking()
            .Where(f => f.BookId != null && !_db.BookTextIndexes.Any(ix => ix.BookId == f.BookId))
            .Select(f => f.BookId).Distinct().CountAsync(ct);

    // Drops the whole index (for a rebuild, or to reclaim space when disabling).
    public async Task<int> ClearAsync(CancellationToken ct)
    {
        return await _db.BookTextIndexes.ExecuteDeleteAsync(ct);
    }

    public sealed record SearchHit(
        int BookId, string Title, int? FirstPublishYear, int? AuthorId, string? AuthorName,
        int? CoverId, bool Owned, string Snippet);

    public sealed record SearchResponse(bool Enabled, string Query, IReadOnlyList<SearchHit> Hits);

    public async Task<SearchResponse> SearchAsync(string? query, int limit, CancellationToken ct)
    {
        var q = query?.Trim() ?? "";
        if (!await IsEnabledAsync(ct)) return new SearchResponse(false, q, Array.Empty<SearchHit>());
        if (q.Length < 2) return new SearchResponse(true, q, Array.Empty<SearchHit>());

        var like = $"%{q}%";
        var rows = await _db.BookTextIndexes.AsNoTracking()
            .Where(ix => EF.Functions.Like(ix.Content, like)
                      || EF.Functions.Like(ix.Book.Title, like)
                      || (ix.Book.Author != null && EF.Functions.Like(ix.Book.Author.Name, like)))
            .OrderBy(ix => ix.Book.Title)
            .Take(limit)
            .Select(ix => new
            {
                ix.BookId, ix.Book.Title, ix.Book.FirstPublishYear, ix.AuthorId,
                AuthorName = ix.Book.Author != null ? ix.Book.Author.Name : null,
                ix.Book.CoverId,
                Owned = ix.Book.ManuallyOwned || ix.Book.LocalFiles.Any(),
                ix.Content,
            })
            .ToListAsync(ct);

        var hits = rows.Select(r => new SearchHit(
            r.BookId, r.Title, r.FirstPublishYear, r.AuthorId, r.AuthorName, r.CoverId, r.Owned,
            Snippet(r.Content, q))).ToList();
        return new SearchResponse(true, q, hits);
    }

    // ~160-char window around the first case-insensitive hit, with ellipses.
    private static string Snippet(string content, string q)
    {
        if (string.IsNullOrEmpty(content)) return "";
        var idx = content.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return content.Length <= 160 ? content : content[..160] + "…";
        var start = Math.Max(0, idx - 70);
        var end = Math.Min(content.Length, idx + q.Length + 70);
        var slice = content[start..end].Replace('\n', ' ').Replace('\r', ' ').Trim();
        return (start > 0 ? "…" : "") + slice + (end < content.Length ? "…" : "");
    }
}

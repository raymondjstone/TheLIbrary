using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Search;

// Optional full-text search over the readable text of matched ebooks. Strictly
// opt-in (AppSettings["FullTextSearchEnabled"], default OFF): extracting and
// storing book text is heavy, so nothing is indexed until the user turns it on.
//
// Indexing runs as a background job (the recurring "index-fulltext" schedule, or
// a manual "run now"), processing up to AppSettings["FullTextIndexMaxPerRun"]
// books per run. It follows the same singleton-coordinator pattern as the other
// scheduled jobs: TryStart launches a background task, IsRunning reports state.
public sealed class FullTextSearchService
{
    public const int MaxCharsPerBook = 200_000;   // ~400 KB of text per book, capped
    public const int DefaultMaxPerRun = 200;      // books indexed per run when unset

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BookTextReader _reader;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly ILogger<FullTextSearchService> _log;

    private volatile bool _isRunning;
    private volatile string? _currentMessage;

    public FullTextSearchService(
        IServiceScopeFactory scopeFactory, BookTextReader reader,
        BackgroundTaskCoordinator coordinator, ILogger<FullTextSearchService> log)
    {
        _scopeFactory = scopeFactory; _reader = reader; _coordinator = coordinator; _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;

    private static async Task<bool> IsEnabledAsync(LibraryDbContext db, CancellationToken ct)
    {
        var v = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.FullTextSearchEnabled)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);
        return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<int> MaxPerRunAsync(LibraryDbContext db, CancellationToken ct)
    {
        var v = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.FullTextIndexMaxPerRun)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);
        return int.TryParse(v, out var n) && n > 0 ? n : DefaultMaxPerRun;
    }

    // --- Background job entry point (schedule + manual "run now") ---------------

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("full-text indexing", out var holder))
        {
            error = $"Another task is already running ({holder})";
            return false;
        }
        error = null;
        _isRunning = true;
        _ = Task.Run(async () =>
        {
            try { await RunIndexAsync(hostCt); }
            catch (OperationCanceledException) when (hostCt.IsCancellationRequested) { }
            catch (Exception ex) { _log.LogError(ex, "Full-text indexing job failed"); }
            finally { _isRunning = false; _currentMessage = null; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    private async Task RunIndexAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        if (!await IsEnabledAsync(db, ct)) { _log.LogInformation("Full-text indexing skipped — feature disabled"); return; }
        var max = await MaxPerRunAsync(db, ct);
        _currentMessage = $"Indexing up to {max} books";
        var result = await IndexBatchCoreAsync(db, max, ct);
        _log.LogInformation("Full-text indexing: indexed {Indexed}, {Remaining} remaining", result.Indexed, result.Remaining);
    }

    // --- Status -----------------------------------------------------------------

    public sealed record IndexStatus(bool Enabled, int Indexed, int Eligible, int MaxPerRun, bool Running, string? Message, DateTime? LastIndexedAt);

    public async Task<IndexStatus> StatusAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var enabled = await IsEnabledAsync(db, ct);
        var max = await MaxPerRunAsync(db, ct);
        var indexed = await db.BookTextIndexes.CountAsync(ct);
        var eligible = await db.LocalBookFiles.AsNoTracking()
            .Where(f => f.BookId != null).Select(f => f.BookId).Distinct().CountAsync(ct);
        var last = await db.BookTextIndexes.AsNoTracking()
            .OrderByDescending(x => x.IndexedAt).Select(x => (DateTime?)x.IndexedAt).FirstOrDefaultAsync(ct);
        return new IndexStatus(enabled, indexed, eligible, max, _isRunning, _currentMessage, last);
    }

    // --- Indexing core ----------------------------------------------------------

    public sealed record IndexResult(int Indexed, int Remaining);

    private async Task<IndexResult> IndexBatchCoreAsync(LibraryDbContext db, int max, CancellationToken ct)
    {
        var candidates = await db.LocalBookFiles.AsNoTracking()
            .Where(f => f.BookId != null && !db.BookTextIndexes.Any(ix => ix.BookId == f.BookId))
            .Select(f => new { BookId = f.BookId!.Value, f.FullPath, f.AuthorId, f.SizeBytes, f.ModifiedAt })
            .Take(max * 4)
            .ToListAsync(ct);

        var byBook = candidates
            .Where(c => BookIntegrityChecker.IsEbook(c.FullPath) && File.Exists(c.FullPath))
            .GroupBy(c => c.BookId).Take(max).ToList();

        var indexed = 0;
        foreach (var grp in byBook)
        {
            ct.ThrowIfCancellationRequested();
            var c = grp.First();
            _currentMessage = $"Indexing {Path.GetFileName(c.FullPath)} ({indexed + 1}/{byBook.Count})";
            string content;
            try { content = await _reader.ReadHeadAsync(c.FullPath, MaxCharsPerBook, ct) ?? ""; }
            catch (Exception ex) { _log.LogDebug(ex, "Full-text index: failed to read {Path}", c.FullPath); content = ""; }

            // Extracted ebook text frequently contains NUL bytes, control chars and
            // broken UTF-16 surrogate pairs that SQL Server rejects on insert. Clean
            // it so one bad book can't blow up the batch.
            var entry = new BookTextIndex
            {
                BookId = c.BookId, AuthorId = c.AuthorId, FullPath = c.FullPath,
                Content = Sanitize(content),
                SizeBytes = c.SizeBytes, ModifiedAt = c.ModifiedAt, IndexedAt = DateTime.UtcNow,
            };
            db.BookTextIndexes.Add(entry);
            try
            {
                await db.SaveChangesAsync(ct);
                indexed++;
            }
            catch (Exception ex)
            {
                // Isolate and skip the offending row, then store an empty placeholder
                // so it isn't retried forever; keep the run going.
                _log.LogWarning(ex, "Full-text index: could not store text for {Path}; storing empty", c.FullPath);
                db.Entry(entry).State = EntityState.Detached;
                entry.Content = "";
                db.BookTextIndexes.Add(entry);
                try { await db.SaveChangesAsync(ct); indexed++; }
                catch (Exception ex2)
                {
                    _log.LogError(ex2, "Full-text index: giving up on {Path}", c.FullPath);
                    db.Entry(entry).State = EntityState.Detached;
                }
            }
        }

        var remaining = await db.LocalBookFiles.AsNoTracking()
            .Where(f => f.BookId != null && !db.BookTextIndexes.Any(ix => ix.BookId == f.BookId))
            .Select(f => f.BookId).Distinct().CountAsync(ct);
        return new IndexResult(indexed, remaining);
    }

    public async Task<int> ClearAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        return await db.BookTextIndexes.ExecuteDeleteAsync(ct);
    }

    // --- Search -----------------------------------------------------------------

    public sealed record SearchHit(
        int BookId, string Title, int? FirstPublishYear, int? AuthorId, string? AuthorName,
        int? CoverId, bool Owned, string Snippet);

    public sealed record SearchResponse(bool Enabled, string Query, IReadOnlyList<SearchHit> Hits);

    public async Task<SearchResponse> SearchAsync(string? query, int limit, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var q = query?.Trim() ?? "";
        if (!await IsEnabledAsync(db, ct)) return new SearchResponse(false, q, Array.Empty<SearchHit>());
        if (q.Length < 2) return new SearchResponse(true, q, Array.Empty<SearchHit>());

        var like = $"%{q}%";
        var rows = await db.BookTextIndexes.AsNoTracking()
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

    // Strips characters SQL Server's nvarchar / the TDS protocol can choke on:
    // NUL, most C0 control chars (keeping tab/newline/return), and unpaired UTF-16
    // surrogates. Returns clean, storable text.
    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (char.IsHighSurrogate(ch))
            {
                // Keep only a well-formed high+low pair; otherwise drop the high half.
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(ch); sb.Append(s[++i]); }
                continue;
            }
            if (char.IsLowSurrogate(ch)) continue;            // unpaired low surrogate
            if (ch == '\t' || ch == '\n' || ch == '\r') { sb.Append(ch); continue; }
            if (ch < 0x20 || ch == 0x7F) { sb.Append(' '); continue; }  // other control chars
            sb.Append(ch);
        }
        return sb.ToString();
    }

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

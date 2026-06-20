using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Search;

// Optional full-text search over the readable text of indexed files. Strictly
// opt-in (AppSettings["FullTextSearchEnabled"], default OFF). By default only
// matched books are indexed; two further opt-in settings extend it to unmatched
// files sitting in author folders and to loose files in the __unknown bucket.
//
// Indexing runs as a background job (the recurring "index-fulltext" schedule, or
// a manual "run now"), processing up to AppSettings["FullTextIndexMaxPerRun"]
// files per run. Same singleton-coordinator pattern as the other scheduled jobs.
public sealed class FullTextSearchService
{
    public const int MaxCharsPerBook = 200_000;   // ~400 KB of text per file, capped
    public const int DefaultMaxPerRun = 200;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BookTextReader _reader;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly ILogger<FullTextSearchService> _log;

    private volatile bool _isRunning;
    private volatile string? _currentMessage;

    // null = not yet probed. true = a SQL Server full-text index exists, so
    // search uses fast CONTAINS. false = not available (FT component not
    // installed / no permission) → fall back to a bounded LIKE scan.
    private bool? _ftAvailable;
    private const string FtCatalog = "TheLibraryFtCatalog";
    private const string FtTable = "BookTextIndexes";

    public FullTextSearchService(
        IServiceScopeFactory scopeFactory, BookTextReader reader,
        BackgroundTaskCoordinator coordinator, ILogger<FullTextSearchService> log)
    {
        _scopeFactory = scopeFactory; _reader = reader; _coordinator = coordinator; _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;

    private sealed record Options(bool Enabled, int MaxPerRun, bool IncludeUnmatched, bool IncludeUnknown);

    private static async Task<Options> ReadOptionsAsync(LibraryDbContext db, CancellationToken ct)
    {
        var rows = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.FullTextSearchEnabled
                     || s.Key == AppSettingKeys.FullTextIndexMaxPerRun
                     || s.Key == AppSettingKeys.FullTextIndexUnmatchedAuthorFiles
                     || s.Key == AppSettingKeys.FullTextIndexUnknownFiles)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        bool Flag(string k) => rows.TryGetValue(k, out var v) && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        var max = rows.TryGetValue(AppSettingKeys.FullTextIndexMaxPerRun, out var m) && int.TryParse(m, out var n) && n > 0 ? n : DefaultMaxPerRun;
        return new Options(Flag(AppSettingKeys.FullTextSearchEnabled), max,
            Flag(AppSettingKeys.FullTextIndexUnmatchedAuthorFiles), Flag(AppSettingKeys.FullTextIndexUnknownFiles));
    }

    // Tries to stand up a SQL Server full-text catalog + index over the text
    // column so search can use CONTAINS. Idempotent and best-effort: if the
    // Full-Text component isn't installed (common on the stock mssql Linux
    // image) or we lack permission, it logs and falls back to LIKE. Each
    // CREATE FULLTEXT … must be the only statement in its batch.
    private async Task<bool> EnsureFtAsync(LibraryDbContext db, CancellationToken ct)
    {
        if (_ftAvailable.HasValue) return _ftAvailable.Value;
        try
        {
            var installed = await db.Database
                .SqlQuery<int>($"SELECT CAST(ISNULL(FULLTEXTSERVICEPROPERTY('IsFullTextInstalled'), 0) AS int) AS [Value]")
                .FirstOrDefaultAsync(ct);
            if (installed != 1) { _ftAvailable = false; return false; }

            var hasIdx = await db.Database
                .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID({"dbo." + FtTable})")
                .FirstOrDefaultAsync(ct);
            if (hasIdx == 0)
            {
                var hasCat = await db.Database
                    .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.fulltext_catalogs WHERE name = {FtCatalog}")
                    .FirstOrDefaultAsync(ct);
                // EF1002 (SQL-injection): these DDL statements interpolate only
                // trusted values — the FtCatalog/FtTable constants and keyIndex,
                // which is an object NAME read from sys.indexes. SQL parameters
                // can't stand in for identifiers (catalog/table/index names), so
                // raw interpolation is required here and is safe.
#pragma warning disable EF1002
                if (hasCat == 0)
                    await db.Database.ExecuteSqlRawAsync($"CREATE FULLTEXT CATALOG {FtCatalog}", ct);

                // Use whatever the primary-key index is actually called.
                var keyIndex = await db.Database
                    .SqlQuery<string>($"SELECT TOP 1 name AS [Value] FROM sys.indexes WHERE object_id = OBJECT_ID({"dbo." + FtTable}) AND is_primary_key = 1")
                    .FirstOrDefaultAsync(ct);
                if (string.IsNullOrEmpty(keyIndex)) { _ftAvailable = false; return false; }

                await db.Database.ExecuteSqlRawAsync(
                    $"CREATE FULLTEXT INDEX ON dbo.{FtTable}(Content, Title) KEY INDEX {keyIndex} ON {FtCatalog} WITH CHANGE_TRACKING AUTO", ct);
#pragma warning restore EF1002
            }
            _ftAvailable = true;
            _log.LogInformation("Full-text search: SQL Server full-text index ready (CONTAINS enabled)");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Full-text search: SQL Server full-text index unavailable; using LIKE fallback");
            _ftAvailable = false;
            return false;
        }
    }

    // Turns a user phrase into a safe CONTAINS prefix predicate: each word
    // becomes "word*", AND-joined. Words are alphanumeric (split on non-word
    // chars), so they're safe to quote.
    private static string? BuildContainsTerm(string q)
    {
        var words = System.Text.RegularExpressions.Regex.Split(q, @"\W+")
            .Where(w => w.Length > 0).ToList();
        if (words.Count == 0) return null;
        return string.Join(" AND ", words.Select(w => $"\"{w}*\""));
    }

    // --- Background job entry point ---------------------------------------------

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
        var opt = await ReadOptionsAsync(db, ct);
        if (!opt.Enabled) { _log.LogInformation("Full-text indexing skipped — feature disabled"); return; }
        var useFt = await EnsureFtAsync(db, ct);   // stand up the FT index up front if possible
        _currentMessage = $"Indexing up to {opt.MaxPerRun} files";
        // Build the inverted word index only when SQL full-text CONTAINS isn't the engine.
        var indexed = await IndexBatchCoreAsync(db, opt, buildWords: !useFt, ct);
        _log.LogInformation("Full-text indexing: indexed {Indexed} files", indexed);
    }

    // --- Status -----------------------------------------------------------------

    public sealed record IndexStatus(
        bool Enabled, bool IncludeUnmatched, bool IncludeUnknown,
        int Indexed, int Eligible, int MaxPerRun, bool Running, string? Message, DateTime? LastIndexedAt,
        string Engine);

    public async Task<IndexStatus> StatusAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var opt = await ReadOptionsAsync(db, ct);
        var indexed = await db.BookTextIndexes.CountAsync(ct);
        var outstanding = await OutstandingAsync(db, opt, ct);
        var last = await db.BookTextIndexes.AsNoTracking()
            .OrderByDescending(x => x.IndexedAt).Select(x => (DateTime?)x.IndexedAt).FirstOrDefaultAsync(ct);
        var engine = !opt.Enabled ? "disabled"
            : (await EnsureFtAsync(db, ct)) ? "SQL Server full-text" : "Word index";
        return new IndexStatus(opt.Enabled, opt.IncludeUnmatched, opt.IncludeUnknown,
            indexed, indexed + outstanding, opt.MaxPerRun, _isRunning, _currentMessage, last, engine);
    }

    private static async Task<int> OutstandingAsync(LibraryDbContext db, Options opt, CancellationToken ct)
    {
        var n = await db.LocalBookFiles.AsNoTracking()
            .Where(f => f.BookId != null && !db.BookTextIndexes.Any(ix => ix.FullPath == f.FullPath))
            .CountAsync(ct);
        if (opt.IncludeUnmatched)
            n += await db.LocalBookFiles.AsNoTracking()
                .Where(f => f.AuthorId != null && f.BookId == null && !db.BookTextIndexes.Any(ix => ix.FullPath == f.FullPath))
                .CountAsync(ct);
        if (opt.IncludeUnknown)
            n += await db.UnknownFiles.AsNoTracking()
                .Where(u => !db.BookTextIndexes.Any(ix => ix.FullPath == u.FullPath))
                .CountAsync(ct);
        return n;
    }

    // --- Indexing core ----------------------------------------------------------

    private sealed record Candidate(string FullPath, TextIndexSource Source, int? BookId, int? AuthorId, string Title, long SizeBytes, DateTime ModifiedAt);

    private async Task<int> IndexBatchCoreAsync(LibraryDbContext db, Options opt, bool buildWords, CancellationToken ct)
    {
        var over = opt.MaxPerRun * 4;
        var candidates = new List<Candidate>();

        // 1. Matched books (always). Starred authors (higher Priority) first.
        var matched = await db.LocalBookFiles.AsNoTracking()
            .Where(f => f.BookId != null && !db.BookTextIndexes.Any(ix => ix.FullPath == f.FullPath))
            .OrderByDescending(f => f.Author!.Priority).ThenBy(f => f.Id)
            .Select(f => new { f.FullPath, f.BookId, f.AuthorId, Title = f.Book!.Title, f.SizeBytes, f.ModifiedAt })
            .Take(over).ToListAsync(ct);
        candidates.AddRange(matched
            .Where(c => BookIntegrityChecker.IsEbook(c.FullPath) && File.Exists(c.FullPath))
            .Select(c => new Candidate(c.FullPath, TextIndexSource.MatchedBook, c.BookId, c.AuthorId,
                c.Title ?? Path.GetFileNameWithoutExtension(c.FullPath), c.SizeBytes, c.ModifiedAt)));

        // 2. Unmatched files in author folders (opt-in).
        if (opt.IncludeUnmatched && candidates.Count < opt.MaxPerRun)
        {
            var unmatched = await db.LocalBookFiles.AsNoTracking()
                .Where(f => f.AuthorId != null && f.BookId == null && !db.BookTextIndexes.Any(ix => ix.FullPath == f.FullPath))
                .OrderByDescending(f => f.Author!.Priority).ThenBy(f => f.Id)
                .Select(f => new { f.FullPath, f.AuthorId, f.TitleFolder, f.SizeBytes, f.ModifiedAt })
                .Take(over).ToListAsync(ct);
            candidates.AddRange(unmatched
                .Where(c => BookIntegrityChecker.IsEbook(c.FullPath) && File.Exists(c.FullPath))
                .Select(c => new Candidate(c.FullPath, TextIndexSource.UnmatchedAuthorFile, null, c.AuthorId,
                    string.IsNullOrWhiteSpace(c.TitleFolder) ? Path.GetFileNameWithoutExtension(c.FullPath) : c.TitleFolder!,
                    c.SizeBytes, c.ModifiedAt)));
        }

        // 3. Loose files in the __unknown quarantine (opt-in).
        if (opt.IncludeUnknown && candidates.Count < opt.MaxPerRun)
        {
            var unknown = await db.UnknownFiles.AsNoTracking()
                .Where(u => !db.BookTextIndexes.Any(ix => ix.FullPath == u.FullPath))
                .Select(u => new { u.FullPath, u.NormalizedTitle, u.FileName, u.SizeBytes, u.ModifiedAt })
                .Take(over).ToListAsync(ct);
            candidates.AddRange(unknown
                .Where(c => BookIntegrityChecker.IsEbook(c.FullPath) && File.Exists(c.FullPath))
                .Select(c => new Candidate(c.FullPath, TextIndexSource.UnknownFile, null, null,
                    string.IsNullOrWhiteSpace(c.NormalizedTitle) ? Path.GetFileNameWithoutExtension(c.FileName) : c.NormalizedTitle!,
                    c.SizeBytes, c.ModifiedAt)));
        }

        var batch = candidates
            .GroupBy(c => c.FullPath).Select(g => g.First())   // de-dupe by path
            .Take(opt.MaxPerRun).ToList();

        // Author names for the batch (so author search works in the word index too).
        var batchAuthorIds = batch.Where(c => c.AuthorId != null).Select(c => c.AuthorId!.Value).Distinct().ToList();
        var authorNames = batchAuthorIds.Count == 0
            ? new Dictionary<int, string>()
            : await db.Authors.AsNoTracking().Where(a => batchAuthorIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, a => a.Name, ct);

        var indexed = 0;
        foreach (var c in batch)
        {
            ct.ThrowIfCancellationRequested();
            _currentMessage = $"Indexing {Path.GetFileName(c.FullPath)} ({indexed + 1}/{batch.Count})";
            string content;
            try { content = await _reader.ReadHeadAsync(c.FullPath, MaxCharsPerBook, ct) ?? ""; }
            catch (Exception ex) { _log.LogDebug(ex, "Full-text index: failed to read {Path}", c.FullPath); content = ""; }
            content = Sanitize(content);
            var authorName = c.AuthorId != null && authorNames.TryGetValue(c.AuthorId.Value, out var an) ? an : null;

            var entry = new BookTextIndex
            {
                FullPath = c.FullPath, Source = c.Source, BookId = c.BookId, AuthorId = c.AuthorId,
                Title = c.Title, Content = content,
                SizeBytes = c.SizeBytes, ModifiedAt = c.ModifiedAt, IndexedAt = DateTime.UtcNow,
            };
            db.BookTextIndexes.Add(entry);
            var stored = false;
            try { await db.SaveChangesAsync(ct); stored = true; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Full-text index: could not store text for {Path}; storing empty", c.FullPath);
                db.Entry(entry).State = EntityState.Detached;
                entry.Content = "";
                db.BookTextIndexes.Add(entry);
                try { await db.SaveChangesAsync(ct); stored = true; }
                catch (Exception ex2) { _log.LogError(ex2, "Full-text index: giving up on {Path}", c.FullPath); db.Entry(entry).State = EntityState.Detached; }
            }
            if (!stored) continue;
            indexed++;

            // Inverted word index (only when not using SQL full-text CONTAINS).
            if (buildWords)
            {
                try { await IndexWordsAsync(db, entry.Id, $"{content} {c.Title} {authorName}", ct); }
                catch (Exception ex) { _log.LogDebug(ex, "Full-text index: word indexing failed for {Path}", c.FullPath); }
            }
        }
        return indexed;
    }

    private const int WordsCapPerFile = 1000;

    private static async Task IndexWordsAsync(LibraryDbContext db, int textIndexId, string text, CancellationToken ct)
    {
        var words = Tokenize(text, WordsCapPerFile);
        if (words.Count == 0) return;
        foreach (var w in words)
            db.TextIndexWords.Add(new TextIndexWord { Word = w, TextIndexId = textIndexId });
        await db.SaveChangesAsync(ct);
    }

    // Distinct lowercase alphanumeric tokens (length 2..64), capped. Enumeration
    // is lazy, so we stop scanning once the cap of distinct words is reached.
    private static List<string> Tokenize(string text, int cap)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text)) return new();
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{Nd}]{2,64}"))
            if (seen.Add(m.Value) && seen.Count >= cap) break;
        return seen.ToList();
    }

    public async Task<int> ClearAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        return await db.BookTextIndexes.ExecuteDeleteAsync(ct);
    }

    // --- Search -----------------------------------------------------------------

    public sealed record SearchHit(
        int? BookId, int? AuthorId, string Title, string? AuthorName, int? FirstPublishYear,
        int? CoverId, bool Owned, string Source, string File, string Snippet);

    public sealed record SearchResponse(bool Enabled, string Query, IReadOnlyList<SearchHit> Hits);

    // `source` filters results to one TextIndexSource; null = all types.
    public async Task<SearchResponse> SearchAsync(string? query, int limit, TextIndexSource? source, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var enabled = (await ReadOptionsAsync(db, ct)).Enabled;
        var q = query?.Trim() ?? "";
        if (!enabled) return new SearchResponse(false, q, Array.Empty<SearchHit>());
        if (q.Length < 2) return new SearchResponse(true, q, Array.Empty<SearchHit>());

        var useFt = await EnsureFtAsync(db, ct);

        IQueryable<BookTextIndex> baseQuery;
        if (useFt && BuildContainsTerm(q) is { } term)
        {
            // Fast path: SQL Server full-text CONTAINS over Content + Title. The
            // author-name match stays a (cheap) LIKE on the small Authors table.
            var like = $"%{q}%";
            baseQuery = db.BookTextIndexes.AsNoTracking()
                .Where(ix => EF.Functions.Contains(ix.Content, term)
                          || EF.Functions.Contains(ix.Title, term)
                          || (ix.Book != null && ix.Book.Author != null && EF.Functions.Like(ix.Book.Author.Name, like)));
        }
        else
        {
            // Fallback: the inverted word index. Each query word is an indexed
            // prefix seek; rows must contain ALL words (AND). This never touches
            // the off-row text column, so it's fast on any SQL Server edition.
            var words = Tokenize(q, 8);
            if (words.Count == 0) return new SearchResponse(true, q, Array.Empty<SearchHit>());
            IQueryable<int>? ids = null;
            foreach (var w in words)
            {
                var pref = w + "%";
                var s = db.TextIndexWords.AsNoTracking()
                    .Where(t => EF.Functions.Like(t.Word, pref)).Select(t => t.TextIndexId).Distinct();
                ids = ids is null ? s : ids.Intersect(s);
            }
            var hitIds = await ids!.Take(2000).ToListAsync(ct);
            baseQuery = db.BookTextIndexes.AsNoTracking().Where(ix => hitIds.Contains(ix.Id));
        }

        if (source.HasValue)
            baseQuery = baseQuery.Where(ix => ix.Source == source.Value);

        var rows = await baseQuery
            .OrderBy(ix => ix.Source).ThenBy(ix => ix.Title)
            .Take(limit)
            .Select(ix => new
            {
                ix.BookId, ix.AuthorId, ix.Source, ix.FullPath, ix.Content,
                Title = ix.BookId != null ? ix.Book!.Title : ix.Title,
                BookAuthorName = ix.BookId != null && ix.Book!.Author != null ? ix.Book.Author.Name : null,
                FirstPublishYear = ix.BookId != null ? ix.Book!.FirstPublishYear : null,
                CoverId = ix.BookId != null ? ix.Book!.CoverId : null,
                Owned = ix.BookId != null && (ix.Book!.ManuallyOwned || ix.Book.OwnedDifferentEdition || ix.Book.LocalFiles.Any()),
            })
            .ToListAsync(ct);

        // Fill author names for non-matched rows that carry an AuthorId.
        var authorIds = rows.Where(r => r.BookAuthorName == null && r.AuthorId != null)
            .Select(r => r.AuthorId!.Value).Distinct().ToList();
        var authorNames = authorIds.Count == 0
            ? new Dictionary<int, string>()
            : await db.Authors.AsNoTracking().Where(a => authorIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, a => a.Name, ct);

        var hits = rows.Select(r => new SearchHit(
            r.BookId, r.AuthorId, r.Title,
            r.BookAuthorName ?? (r.AuthorId != null && authorNames.TryGetValue(r.AuthorId.Value, out var an) ? an : null),
            r.FirstPublishYear, r.CoverId, r.Owned, r.Source.ToString(),
            Path.GetFileName(r.FullPath), Snippet(r.Content, q))).ToList();
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
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(ch); sb.Append(s[++i]); }
                continue;
            }
            if (char.IsLowSurrogate(ch)) continue;
            if (ch == '\t' || ch == '\n' || ch == '\r') { sb.Append(ch); continue; }
            if (ch < 0x20 || ch == 0x7F) { sb.Append(' '); continue; }
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

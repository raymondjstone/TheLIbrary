using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Controllers;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

public sealed record ContentScanSummary(int Scanned, int WithInfo);
public sealed record AuthorScanResult(int Scanned, int Identified, int Errors, int Remaining);

// Reads the front matter of unmatched (book-less) and untracked (__unknown) ebook
// files and guesses author / title / series — stored in BookContentScan, which
// also marks a file "already scanned" so it's read only once (unless it changes).
// Damaged and archived files are skipped. Runs as a capped background job and is
// also driven per-author from the author page.
public sealed class ContentScanService
{
    public const int DefaultMaxPerRun = 50;
    // Front matter (title / copyright / "also by") lives in the first pages; a
    // few dozen pages of prose is ~40k characters.
    private const int HeadChars = 40_000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly BookTextReader _reader;
    private readonly ILogger<ContentScanService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;
    private ContentScanSummary? _lastResult;

    public ContentScanService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        BookTextReader reader,
        ILogger<ContentScanService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _reader = reader;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;
    public ContentScanSummary? LastResult => _lastResult;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("identify books from content", out var holder))
        {
            error = $"Another task is already running ({holder})";
            return false;
        }
        error = null;
        _isRunning = true;
        _ = Task.Run(async () =>
        {
            try { _lastResult = await RunAsync(hostCt); }
            catch (OperationCanceledException) when (hostCt.IsCancellationRequested) { }
            catch (Exception ex) { _log.LogError(ex, "Content-scan job failed"); }
            finally { _isRunning = false; _currentMessage = null; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    // Background per-author run for the author-page button. Runs on the host
    // lifetime token (NOT the HTTP request token) so closing/aborting the request
    // can't cancel the scan — a long Calibre-heavy run no longer fails the button.
    public bool TryStartAuthor(int authorId, CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire($"identify books for author {authorId}", out var holder))
        {
            error = $"Another task is already running ({holder})";
            return false;
        }
        error = null;
        _isRunning = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var r = await ScanAuthorAsync(authorId, hostCt);
                _log.LogInformation("Per-author content scan done (author {Id}) — scanned {Scanned}, identified {Identified}, errors {Errors}, remaining {Remaining}",
                    authorId, r.Scanned, r.Identified, r.Errors, r.Remaining);
            }
            catch (OperationCanceledException) when (hostCt.IsCancellationRequested) { }
            catch (Exception ex) { _log.LogError(ex, "Per-author content scan failed for author {Id}", authorId); }
            finally { _isRunning = false; _currentMessage = null; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    internal Task<ContentScanSummary> RunForTestsAsync(CancellationToken ct) => RunAsync(ct);

    private async Task<ContentScanSummary> RunAsync(CancellationToken ct)
    {
        _log.LogInformation("Content-scan job starting");
        _currentMessage = "Loading files to identify";
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

        var max = await LoadMaxPerRunAsync(db, ct);
        var archiveLeaf = await LoadArchiveLeafAsync(db, ct);
        var notArchived = ArchivedFilesController.NotUnderArchive(archiveLeaf);
        var untrackedFirst = await LoadUntrackedFirstAsync(db, ct);

        // Drop guesses whose files are gone. BookContentScan rows are keyed by
        // FullPath, but several flows move or delete files without touching
        // them (flatten, return-to-incoming, manual deletes, accept-author
        // before it updated the row) — leaving the Identified page showing
        // entries for locations the files are no longer at.
        _currentMessage = "Pruning stale identified rows";
        var pruned = await PruneStaleRowsAsync(db, ct);
        if (pruned > 0)
            _log.LogInformation("Content-scan: removed {Count} stale identified row(s) whose files moved or were deleted", pruned);

        // Cheap DB-only pass first: untracked rows whose CONTENT yielded no author
        // (DRM'd AZW3s, prose-from-line-one .txt) often carry it in the FILENAME
        // ("The Star by Arthur C. Clarke.txt"). Catalogue-validated, so it also
        // repairs rows scanned before filename parsing existed.
        _currentMessage = "Reading author/title from untracked filenames";
        var enriched = await EnrichUntrackedFromFilenamesAsync(db, ct);
        if (enriched > 0)
            _log.LogInformation("Content-scan: filled {Count} untracked guesses from filenames", enriched);

        // Process in priority tiers, each filling whatever capacity the previous
        // ones left (mirrors the aims of this job):
        //   A. UNMATCHED books that sit in an author folder — match them to the
        //      right book. Starred authors (higher Author.Priority) first.
        //   B. UNTRACKED files in the __unknown bucket — match them to an author.
        //   C. THE REST — unmatched files with no author folder at all.
        // When UntrackedFirst is set the order becomes B → A → C.
        var unmatchedBase = db.LocalBookFiles.AsNoTracking()
            .Where(f => f.BookId == null && f.IntegrityOk != false)
            .Where(LbfTextBearing)
            .Where(notArchived)
            .Where(f => !db.BookContentScans.Any(c => c.FullPath == f.FullPath && c.SizeBytes == f.SizeBytes));

        async Task<List<ScanItem>> TierAuthorLinked(int remaining) =>
            await unmatchedBase
                .Where(f => f.AuthorId != null)
                .OrderByDescending(f => f.Author!.Priority)
                .ThenBy(f => f.Id)
                .Take(remaining)
                .Select(f => new ScanItem(f.FullPath, f.SizeBytes, f.ModifiedAt, "unmatched", f.AuthorId))
                .ToListAsync(ct);

        async Task<List<ScanItem>> TierUntracked(int remaining) =>
            await db.UnknownFiles.AsNoTracking()
                .Where(UfTextBearing)
                // Size-qualified like the unmatched tiers, so an untracked file
                // that changed since its last scan is read again.
                .Where(u => !db.BookContentScans.Any(c => c.FullPath == u.FullPath && c.SizeBytes == u.SizeBytes))
                .OrderBy(u => u.Id)
                .Take(remaining)
                .Select(u => new ScanItem(u.FullPath, u.SizeBytes, u.ModifiedAt, "untracked", null))
                .ToListAsync(ct);

        async Task<List<ScanItem>> TierOrphan(int remaining) =>
            await unmatchedBase
                .Where(f => f.AuthorId == null)
                .OrderBy(f => f.Id)
                .Take(remaining)
                .Select(f => new ScanItem(f.FullPath, f.SizeBytes, f.ModifiedAt, "unmatched", f.AuthorId))
                .ToListAsync(ct);

        List<ScanItem> items;
        if (untrackedFirst)
        {
            // Tier 1: untracked __unknown files.
            items = await TierUntracked(max);
            // Tier 2: author-linked unmatched.
            if (items.Count < max)
                items = items.Concat(await TierAuthorLinked(max - items.Count)).ToList();
        }
        else
        {
            // Tier 1: author-linked unmatched, starred authors first.
            items = await TierAuthorLinked(max);
            // Tier 2: untracked __unknown files.
            if (items.Count < max)
                items = items.Concat(await TierUntracked(max - items.Count)).ToList();
        }

        // Final tier (both modes): orphan unmatched with no author folder.
        if (items.Count < max)
            items = items.Concat(await TierOrphan(max - items.Count)).ToList();

        if (items.Count == 0)
        {
            _currentMessage = "Done — nothing to identify";
            return new ContentScanSummary(0, 0);
        }

        var withInfo = 0;
        var scanned = 0;
        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            _currentMessage = $"Identifying {i + 1}/{items.Count}: {Path.GetFileName(items[i].FullPath)}";
            try
            {
                if (await ScanOneAsync(db, items[i], ct)) withInfo++;
                await db.SaveChangesAsync(ct);
                scanned++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Content-scan: failed on {Path}", items[i].FullPath);
                db.ChangeTracker.Clear();
            }
        }

        _log.LogInformation("Content-scan job done — scanned {Scanned}, with info {WithInfo}", scanned, withInfo);
        _currentMessage = $"Done — scanned {scanned}, identified {withInfo}";
        return new ContentScanSummary(scanned, withInfo);
    }

    // Per-author run for the author-page button: scans up to `max` of this
    // author's not-yet-scanned unmatched files (capped per run). Resilient — one
    // bad file is logged and skipped, never aborts the rest — and reports how
    // many were scanned, how many yielded info, and how many still remain.
    public async Task<AuthorScanResult> ScanAuthorAsync(int authorId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var max = await LoadMaxPerRunAsync(db, ct);
        var archiveLeaf = await LoadArchiveLeafAsync(db, ct);
        var notArchived = ArchivedFilesController.NotUnderArchive(archiveLeaf);

        // Predicate for "this author's unscanned, text-bearing, unmatched files".
        IQueryable<LocalBookFile> Candidates() => db.LocalBookFiles.AsNoTracking()
            .Where(f => f.AuthorId == authorId && f.BookId == null && f.IntegrityOk != false)
            .Where(LbfTextBearing)
            .Where(notArchived)
            .Where(f => !db.BookContentScans.Any(c => c.FullPath == f.FullPath && c.SizeBytes == f.SizeBytes));

        var totalBefore = await Candidates().CountAsync(ct);
        var items = await Candidates()
            .OrderBy(f => f.Id)
            .Take(max)
            .Select(f => new ScanItem(f.FullPath, f.SizeBytes, f.ModifiedAt, "unmatched", f.AuthorId))
            .ToListAsync(ct);

        var scanned = 0;
        var identified = 0;
        var errors = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            _currentMessage = $"Identifying {scanned + errors + 1}/{items.Count}: {Path.GetFileName(item.FullPath)}";
            try
            {
                if (await ScanOneAsync(db, item, ct)) identified++;
                await db.SaveChangesAsync(ct); // per file so one bad row can't lose the rest
                scanned++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Content-scan: failed on {Path}", item.FullPath);
                db.ChangeTracker.Clear(); // drop the failed pending change, keep going
                errors++;
            }
        }

        var remaining = Math.Max(0, totalBefore - scanned - errors);
        return new AuthorScanResult(scanned, identified, errors, remaining);
    }

    private Task<bool> ScanOneAsync(LibraryDbContext db, ScanItem item, CancellationToken ct)
        => ScanFileAsync(db, item, catalogueOnly: false, ct);

    // Reads a file's front+back matter, parses it, and upserts its BookContentScan
    // row. When catalogueOnly is set, only the series catalogue is stored (the
    // title/author are already known for matched books, so we keep just the part
    // that builds series data). Does NOT SaveChanges — the caller batches that.
    // Returns true when something worth reviewing was found.
    private async Task<bool> ScanFileAsync(LibraryDbContext db, ScanItem item, bool catalogueOnly, CancellationToken ct)
    {
        // Read both ends of the book: title/copyright sit at the front, but the
        // "Also by / Novels by" bibliography and series listings are usually at
        // the back — reading head-only is why series extraction was so patchy.
        var text = await _reader.ReadHeadAndTailAsync(item.FullPath, HeadChars, ct);
        var det = FrontMatterExtractor.Parse(text);

        var row = await db.BookContentScans.FirstOrDefaultAsync(c => c.FullPath == item.FullPath, ct)
                  ?? AddRow(db, item.FullPath);
        row.SizeBytes = item.SizeBytes;
        row.ModifiedAt = item.ModifiedAt;
        row.Source = item.Source;
        row.AuthorId = item.AuthorId;
        row.SeriesCatalogJson = det.SeriesCatalog.Count > 0
            ? System.Text.Json.JsonSerializer.Serialize(det.SeriesCatalog)
            : null;

        if (catalogueOnly)
        {
            // Matched book: title/author/ISBN are already known — keep only the
            // series catalogue so we don't surface redundant "needs matching" rows.
            row.Isbn = row.Title = row.Author = row.Series = row.SeriesPosition = row.AlsoByTitles = null;
            row.ScannedAt = DateTime.UtcNow;
            return det.SeriesCatalog.Count > 0;
        }

        // Clamp to the column lengths so an over-long guess can't fail SaveChanges.
        row.Isbn = Cap(det.Isbn, 20);
        row.Title = Cap(det.Title, 500);
        row.Author = Cap(det.Author, 500);
        row.Series = Cap(det.Series, 500);
        row.SeriesPosition = Cap(det.SeriesPosition, 50);
        row.AlsoByTitles = det.AlsoByTitles.Count > 0 ? Cap(string.Join(";", det.AlsoByTitles), 2000) : null;
        row.ScannedAt = DateTime.UtcNow;

        // The file's OWN embedded metadata (EPUB OPF dc:creator/dc:title, MOBI
        // EXTH, FB2, PDF info) beats both prose parsing and the filename: the
        // quarantine is full of files whose filename title is truncated to ~30
        // chars while the OPF carries the full title and a clean author. Only a
        // catalogue-validated author is trusted, so swapped/garbage metadata
        // ("Dark Prince" as dc:creator) fails validation and falls through to
        // the existing guesses.
        var embedded = FileMetadataReader.TryRead(item.FullPath, _log);
        if (embedded is not null
            && await AuthorNameValidator.ValidateAsync(db, embedded.Author, ct) is { } embeddedAuthor)
        {
            row.Author = Cap(embeddedAuthor, 500);
            if (!string.IsNullOrWhiteSpace(embedded.Title)) row.Title = Cap(embedded.Title, 500);
            row.Series ??= Cap(embedded.Series, 500);
            row.SeriesPosition ??= Cap(embedded.SeriesPosition, 50);
            row.Isbn ??= Cap(embedded.Isbn, 20);
        }

        // Content gave no author but the filename may carry one ("Title - Author",
        // "Title by Author", "Last, First - Series NN - Title"). Only a guess whose
        // author matches the OL/watchlist catalogue is taken, so the orientation
        // ambiguity ("Author - Title" vs "Title - Author") resolves itself.
        if (item.Source == "untracked" && row.Author is null
            && await ValidateFilenameGuessAsync(db, item.FullPath, ct) is { } fg)
        {
            row.Author = Cap(fg.Author, 500);
            row.Title ??= Cap(fg.Title, 500);
            row.Series ??= Cap(fg.Series, 500);
            row.SeriesPosition ??= Cap(fg.SeriesPosition, 50);
        }

        // Pre-provision a Pending Author row for every OL author that matches
        // the guessed name so it is available for selection on the Identified page
        // without the user having to trigger an OL lookup manually.
        if (!string.IsNullOrWhiteSpace(row.Author))
            await EnsurePendingAuthorsForGuessAsync(db, row.Author, ct);

        return det.HasAnything || row.Author is not null;
    }

    // Removes UNREVIEWED scan rows whose path no longer belongs to any tracked
    // index: LocalBookFiles (matched/unmatched files) or UnknownFiles (the
    // untracked quarantine index, rebuilt from disk on every sync). A pure DB
    // pass — no disk I/O. Reviewed rows are kept even when stale: ones carrying
    // a series catalogue still feed apply-catalog after their file has moved.
    // The file's new location gets scanned fresh as its own row.
    internal async Task<int> PruneStaleRowsAsync(LibraryDbContext db, CancellationToken ct)
    {
        var stale = await db.BookContentScans
            .Where(c => !c.Reviewed
                && !db.LocalBookFiles.Any(f => f.FullPath == c.FullPath)
                && !db.UnknownFiles.Any(u => u.FullPath == c.FullPath))
            .ToListAsync(ct);
        if (stale.Count == 0) return 0;
        db.BookContentScans.RemoveRange(stale);
        await db.SaveChangesAsync(ct);
        return stale.Count;
    }

    // Fills missing author/title on EXISTING unreviewed untracked rows from their
    // filenames — no disk I/O, so the whole backlog is done in one pass. Only
    // rows with no author guess at all are touched, and only with a
    // catalogue-validated name; content-derived guesses are never overwritten.
    internal async Task<int> EnrichUntrackedFromFilenamesAsync(LibraryDbContext db, CancellationToken ct)
    {
        var rows = await db.BookContentScans
            .Where(c => !c.Reviewed && c.Source == "untracked" && c.AuthorId == null && c.Author == null)
            .ToListAsync(ct);

        var enriched = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var fg = await ValidateFilenameGuessAsync(db, row.FullPath, ct);
            if (fg is null) continue;
            row.Author = Cap(fg.Author, 500);
            row.Title ??= Cap(fg.Title, 500);
            row.Series ??= Cap(fg.Series, 500);
            row.SeriesPosition ??= Cap(fg.SeriesPosition, 50);
            await EnsurePendingAuthorsForGuessAsync(db, fg.Author!, ct);
            enriched++;
            if (enriched % 100 == 0) await db.SaveChangesAsync(ct);
        }

        // Rows whose content gave an AUTHOR but no title are equally stuck —
        // without a title the assigner's OL work search never runs, so an
        // author missing from the local OL dump can never be confirmed. The
        // filename usually has the title ("Through Death - Parker Jaysen.azw3"):
        // take it from the interpretation that AGREES with the author guess.
        var titleless = await db.BookContentScans
            .Where(c => !c.Reviewed && c.Source == "untracked" && c.AuthorId == null
                && c.Author != null && c.Title == null)
            .ToListAsync(ct);
        foreach (var row in titleless)
        {
            ct.ThrowIfCancellationRequested();
            var wanted = TitleNormalizer.NormalizeAuthor(row.Author!);
            var g = FilenameGuesser.Interpret(row.FullPath).FirstOrDefault(x =>
                x.Title is not null && x.Author is not null
                && TitleNormalizer.NormalizeAuthor(x.Author) == wanted);
            if (g is null) continue;
            row.Title = Cap(g.Title, 500);
            row.Series ??= Cap(g.Series, 500);
            row.SeriesPosition ??= Cap(g.SeriesPosition, 50);
            enriched++;
            if (enriched % 100 == 0) await db.SaveChangesAsync(ct);
        }

        await db.SaveChangesAsync(ct);
        return enriched;
    }

    // Picks the first filename interpretation whose author is a real, known name:
    // an OpenLibrary-catalogue match or an existing watchlist author — never a
    // blacklisted one. An unvalidated name is refused outright (same rule as the
    // assigner), so "Title - Author" vs "Author - Title" can't fill in garbage.
    private static async Task<FilenameGuess?> ValidateFilenameGuessAsync(
        LibraryDbContext db, string fullPath, CancellationToken ct)
    {
        foreach (var g in FilenameGuesser.Interpret(fullPath))
        {
            if (await AuthorNameValidator.ValidateAsync(db, g.Author, ct) is not null) return g;
        }
        return null;
    }

    // Folded into the integrity job: that job has just opened/read this file, so
    // running the content check now (rather than re-reading it in a later
    // content-scan run) is far cheaper. Skips files already scanned at their
    // current size and anything we can't extract text from. For a matched book
    // only the series catalogue is harvested; an unmatched file gets the full
    // guess. Never throws — content extraction must not break the integrity job.
    public async Task ExtractDuringIntegrityAsync(LibraryDbContext db, LocalBookFile file, CancellationToken ct)
    {
        try
        {
            if (!IsTextBearing(file.FullPath)) return;
            var already = await db.BookContentScans
                .AnyAsync(c => c.FullPath == file.FullPath && c.SizeBytes == file.SizeBytes, ct);
            if (already) return;

            var source = file.BookId != null ? "matched" : "unmatched";
            var item = new ScanItem(file.FullPath, file.SizeBytes, file.ModifiedAt, source, file.AuthorId);
            await ScanFileAsync(db, item, catalogueOnly: file.BookId != null, ct);
        }
        catch (Exception ex)
        {
            // Must not disturb the integrity job's own pending changes on this same
            // context — just log and move on (the row is built synchronously after
            // the last await, so there's no partial write to undo).
            _log.LogDebug(ex, "Content extraction during integrity failed for {Path}", file.FullPath);
        }
    }

    // Mirrors LbfTextBearing for a single path (the integrity job already has the
    // entity in hand, so no SQL translation is needed here).
    private static bool IsTextBearing(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext is "epub" or "pdf" or "fb2" or "docx" or "odt" or "rtf" or "txt"
            or "mobi" or "azw" or "azw3" or "lit";
    }

    // Looks up every OpenLibraryAuthor row whose normalized name matches the
    // guessed author string and ensures a Pending watchlist Author row exists for
    // each one. When the OL catalogue contains multiple authors with the same name
    // (common for authors who share a name) ALL of them are pre-provisioned so the
    // user can choose the right one on the Identified page. Existing Author rows
    // (any status) are left untouched. Does NOT call SaveChanges — the scan loop
    // batches that.
    private static async Task EnsurePendingAuthorsForGuessAsync(
        LibraryDbContext db,
        string guessedAuthor,
        CancellationToken ct)
    {
        var normalized = TitleNormalizer.NormalizeAuthor(guessedAuthor.Trim());
        if (string.IsNullOrWhiteSpace(normalized)) return;

        // Names the user explicitly excluded must not be resurrected as Pending
        // rows by a mere content guess (same rule as the adopt-unknown job).
        if (await db.AuthorBlacklist.AsNoTracking().AnyAsync(b => b.NormalizedName == normalized, ct))
            return;

        var olMatches = await db.OpenLibraryAuthors.AsNoTracking()
            .Where(o => o.NormalizedName == normalized)
            .ToListAsync(ct);
        if (olMatches.Count == 0) return;

        // Fetch all existing Author rows that share any of the matched OL keys so we
        // can skip keys already represented without N individual DB round-trips.
        var matchedKeys = olMatches.Select(o => o.OlKey).ToList();
        var existingKeys = await db.Authors.AsNoTracking()
            .Where(a => a.OpenLibraryKey != null && matchedKeys.Contains(a.OpenLibraryKey))
            .Select(a => a.OpenLibraryKey!)
            .ToListAsync(ct);
        var existingKeySet = new HashSet<string>(existingKeys, StringComparer.OrdinalIgnoreCase);

        foreach (var ol in olMatches)
        {
            if (existingKeySet.Contains(ol.OlKey)) continue;
            db.Authors.Add(new Author
            {
                Name = ol.Name,
                OpenLibraryKey = ol.OlKey,
                Status = AuthorStatus.Pending,
            });
            existingKeySet.Add(ol.OlKey); // guard against duplicate OL rows in same batch
        }
    }

    private static string? Cap(string? s, int max)
        => s is null ? null : s.Length <= max ? s : s[..max];

    private static BookContentScan AddRow(LibraryDbContext db, string fullPath)
    {
        var row = new BookContentScan { FullPath = fullPath };
        db.BookContentScans.Add(row);
        return row;
    }

    // Formats BookTextReader can pull text from (excludes cbz/cbr/djvu/doc).
    // Expressions so EF translates them to SQL LIKE filters.
    private static readonly System.Linq.Expressions.Expression<Func<LocalBookFile, bool>> LbfTextBearing =
        f => f.FullPath.EndsWith(".epub") || f.FullPath.EndsWith(".pdf") || f.FullPath.EndsWith(".fb2")
          || f.FullPath.EndsWith(".docx") || f.FullPath.EndsWith(".odt") || f.FullPath.EndsWith(".rtf")
          || f.FullPath.EndsWith(".txt") || f.FullPath.EndsWith(".mobi") || f.FullPath.EndsWith(".azw")
          || f.FullPath.EndsWith(".azw3") || f.FullPath.EndsWith(".lit");

    private static readonly System.Linq.Expressions.Expression<Func<UnknownFile, bool>> UfTextBearing =
        u => u.FullPath.EndsWith(".epub") || u.FullPath.EndsWith(".pdf") || u.FullPath.EndsWith(".fb2")
          || u.FullPath.EndsWith(".docx") || u.FullPath.EndsWith(".odt") || u.FullPath.EndsWith(".rtf")
          || u.FullPath.EndsWith(".txt") || u.FullPath.EndsWith(".mobi") || u.FullPath.EndsWith(".azw")
          || u.FullPath.EndsWith(".azw3") || u.FullPath.EndsWith(".lit");

    private sealed record ScanItem(string FullPath, long SizeBytes, DateTime ModifiedAt, string Source, int? AuthorId);

    private static async Task<int> LoadMaxPerRunAsync(LibraryDbContext db, CancellationToken ct)
    {
        var raw = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.ContentScanMaxPerRun)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);
        return int.TryParse(raw, out var n) && n > 0 ? n : DefaultMaxPerRun;
    }

    private static async Task<string> LoadArchiveLeafAsync(LibraryDbContext db, CancellationToken ct)
    {
        var raw = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.DedupeArchiveFolder)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);
        return (string.IsNullOrWhiteSpace(raw) ? "__archive" : raw.Trim()).Replace('\\', '/').TrimEnd('/');
    }

    private static async Task<bool> LoadUntrackedFirstAsync(LibraryDbContext db, CancellationToken ct)
    {
        var raw = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.ContentScanUntrackedFirst)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);
        return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }
}

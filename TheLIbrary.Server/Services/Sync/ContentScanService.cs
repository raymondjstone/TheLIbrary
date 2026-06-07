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

        // Process in priority tiers, each filling whatever capacity the previous
        // ones left (mirrors the aims of this job):
        //   1. UNMATCHED books that sit in an author folder — match them to the
        //      right book. Starred authors (higher Author.Priority) first.
        //   2. UNTRACKED files in the __unknown bucket — match them to an author.
        //   3. THE REST — unmatched files with no author folder at all.
        var unmatchedBase = db.LocalBookFiles.AsNoTracking()
            .Where(f => f.BookId == null && f.IntegrityOk != false)
            .Where(LbfTextBearing)
            .Where(notArchived)
            .Where(f => !db.BookContentScans.Any(c => c.FullPath == f.FullPath && c.SizeBytes == f.SizeBytes));

        // Tier 1: author-linked unmatched, starred authors first.
        var items = await unmatchedBase
            .Where(f => f.AuthorId != null)
            .OrderByDescending(f => f.Author!.Priority)
            .ThenBy(f => f.Id)
            .Take(max)
            .Select(f => new ScanItem(f.FullPath, f.SizeBytes, f.ModifiedAt, "unmatched", f.AuthorId))
            .ToListAsync(ct);

        // Tier 2: untracked __unknown files.
        if (items.Count < max)
        {
            var untracked = await db.UnknownFiles.AsNoTracking()
                .Where(UfTextBearing)
                .Where(u => !db.BookContentScans.Any(c => c.FullPath == u.FullPath && c.SizeBytes == u.SizeBytes))
                .OrderBy(u => u.Id)
                .Take(max - items.Count)
                .Select(u => new ScanItem(u.FullPath, u.SizeBytes, u.ModifiedAt, "untracked", null))
                .ToListAsync(ct);
            items = items.Concat(untracked).ToList();
        }

        // Tier 3: the rest — unmatched files not in any author folder.
        if (items.Count < max)
        {
            var orphan = await unmatchedBase
                .Where(f => f.AuthorId == null)
                .OrderBy(f => f.Id)
                .Take(max - items.Count)
                .Select(f => new ScanItem(f.FullPath, f.SizeBytes, f.ModifiedAt, "unmatched", f.AuthorId))
                .ToListAsync(ct);
            items = items.Concat(orphan).ToList();
        }

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
        return det.HasAnything;
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
}

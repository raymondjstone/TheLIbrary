using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

public sealed record ManualBookPromotionSummary(
    int Examined, int Promoted, int Merged, int NotFound, int Errors, int Remaining);

// Scheduled job: searches OpenLibrary for every manually-catalogued book
// (synthetic "XX" work key — hand-added entries and the placeholder books the
// series builder mints) and, when OL now lists the title under the same
// author, promotes the manual row to the real OL work.
//
// The author-refresh job already promotes manual rows in place, but only for
// authors that get refreshed (OL-keyed, on the refresh cycle). This job covers
// the rest by searching per book. Promotion is IN PLACE — the Book.Id is kept,
// so the row's series link, position, read status, ownership, files and cover
// all carry over; only the OL-sourced fields are refreshed. When the author
// already has a row for that OL work, the manual row is MERGED into it instead:
// series/position/ownership/read-status/files move across (nothing the user
// set is lost) and the manual duplicate is deleted.
//
// A match needs both the title (normalized-equal, or very close) and the
// author (OL key when we have one, else normalized name) to agree — a search
// hit on title alone must never rebind a book to someone else's work.
public sealed class ManualBookPromotionService
{
    public const int MaxPerRun = 100; // OL search per book — rate-limited.
    private const double TitleFuzzyFloor = 0.92;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly ILogger<ManualBookPromotionService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;
    private ManualBookPromotionSummary? _lastResult;

    public ManualBookPromotionService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        ILogger<ManualBookPromotionService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;
    public ManualBookPromotionSummary? LastResult => _lastResult;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("promote manual books", out var holder))
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
            catch (Exception ex)
            {
                _log.LogError(ex, "Promote-manual-books job failed");
                _currentMessage = $"Failed: {ex.Message}";
            }
            finally { _isRunning = false; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    internal Task<ManualBookPromotionSummary> RunForTestsAsync(CancellationToken ct) => RunAsync(ct);

    private async Task<ManualBookPromotionSummary> RunAsync(CancellationToken ct)
    {
        _log.LogInformation("Promote-manual-books job starting");
        _currentMessage = "Loading manual books";
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var ol = scope.ServiceProvider.GetRequiredService<OpenLibraryClient>();

        // Phase 0 — DB only, no OpenLibrary. A manual placeholder that duplicates a
        // real OL book already under the same author (the series builder mints these)
        // doesn't need a web search to resolve: we already hold the canonical row, so
        // fold it straight in. Unbounded (no OL calls → not subject to the per-run
        // cap), so it clears the whole duplicate backlog every run.
        var dbMerged = await MergeManualDuplicatesIntoRealBooksAsync(db, ct);

        var maxPerRun = await JobRunLimits.GetAsync(db, AppSettingKeys.PromoteManualBooksMaxPerRun, MaxPerRun, ct);

        // Take the books whose OL check is OLDEST — NULL (never checked) sorts first
        // ascending — and stamp PromoteCheckedAt on each as we go. Persisted, so the
        // sweep keeps advancing across restarts and, once every book has had a turn,
        // naturally loops back to re-check the not-yet-on-OL ones oldest-first.
        var toAttempt = await db.Books
            .Where(b => b.OpenLibraryWorkKey.StartsWith(ManualWorkKey.Prefix))
            .OrderBy(b => b.PromoteCheckedAt)
            .ThenBy(b => b.Id)
            .Take(maxPerRun)
            .Select(b => b.Id)
            .ToListAsync(ct);
        var checkedAt = DateTime.UtcNow;

        int examined = 0, promoted = 0, notFound = 0, errors = 0;
        var merged = dbMerged; // total includes the DB-only merges above
        for (var i = 0; i < toAttempt.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var book = await db.Books.Include(b => b.Author)
                .FirstOrDefaultAsync(b => b.Id == toAttempt[i], ct);
            if (book is null || !ManualWorkKey.IsManual(book.OpenLibraryWorkKey)) continue;

            examined++;
            // Stamp the attempt up front so the row rotates to the back of the queue
            // whatever the outcome (found, not found, or error) — every code path below
            // either saves the row or, on error, re-stamps it.
            book.PromoteCheckedAt = checkedAt;
            _currentMessage = $"Checking {i + 1}/{toAttempt.Count}: {book.Author.Name} — {book.Title}"
                + $" ({promoted + merged} linked)";
            try
            {
                var doc = await FindOpenLibraryWorkAsync(ol, book, ct);
                if (doc is null)
                {
                    notFound++;
                    await db.SaveChangesAsync(ct); // persist the check timestamp
                    continue;
                }

                var workKey = doc.Key!.Split('/').Last();
                var existing = await db.Books.FirstOrDefaultAsync(
                    b => b.Id != book.Id && b.AuthorId == book.AuthorId && b.OpenLibraryWorkKey == workKey, ct);

                if (existing is not null)
                {
                    await MergeIntoAsync(db, book, existing, ct);
                    merged++;
                    _log.LogInformation(
                        "Promote-manual-books: merged manual \"{Title}\" into existing OL work {Key} for {Author}",
                        book.Title, workKey, book.Author.Name);
                }
                else
                {
                    // In-place promotion — same semantics as the author refresh:
                    // keep the row (and so its series, files, read status,
                    // ownership), refresh the OL-sourced fields.
                    book.OpenLibraryWorkKey = workKey;
                    if (!string.IsNullOrWhiteSpace(doc.Title))
                    {
                        book.Title = doc.Title!;
                        book.NormalizedTitle = TitleNormalizer.Normalize(doc.Title!);
                    }
                    if (doc.FirstPublishYear is not null) book.FirstPublishYear = doc.FirstPublishYear;
                    if (doc.CoverId is not null) book.CoverId = doc.CoverId;
                    // Re-date a PAST-year book to 1 Jan of its publish year so it
                    // doesn't surface as a new release (the manual row kept the date
                    // from when it was hand-added / minted as a placeholder); leave a
                    // this-year/unknown one on its live date.
                    if (Book.CreatedAtForPublishYear(book.FirstPublishYear) is { } promotedDate)
                        book.CreatedAt = promotedDate;
                    promoted++;
                    _log.LogInformation(
                        "Promote-manual-books: linked \"{Title}\" to OL work {Key} for {Author}",
                        book.Title, workKey, book.Author.Name);
                }

                await db.SaveChangesAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                errors++;
                var failedId = book.Id;
                db.ChangeTracker.Clear();
                _log.LogWarning(ex, "Promote-manual-books: failed on \"{Title}\" (id {Id})", book.Title, failedId);
                // The tracked timestamp was discarded with the tracker — stamp it
                // directly so a persistently-failing book still rotates and can't
                // wedge the front of the oldest-first queue.
                try { await db.Books.Where(b => b.Id == failedId)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.PromoteCheckedAt, checkedAt), ct); }
                catch (Exception ex2) { _log.LogWarning(ex2, "Promote-manual-books: could not stamp check time on id {Id}", failedId); }
            }
        }

        var remaining = await db.Books
            .CountAsync(b => b.OpenLibraryWorkKey.StartsWith(ManualWorkKey.Prefix), ct);
        var summary = new ManualBookPromotionSummary(examined, promoted, merged, notFound, errors, remaining);
        _log.LogInformation(
            "Promote-manual-books job done — examined {Examined}, promoted {Promoted}, merged {Merged} ({DbMerged} from DB), not on OL {NotFound}, errors {Errors}, remaining {Remaining}",
            examined, promoted, merged, dbMerged, notFound, errors, remaining);
        _currentMessage = $"Done — {promoted} promoted, {merged} merged ({dbMerged} from DB matches), {notFound} not on OL yet, {remaining} manual book(s) remaining";
        return summary;
    }

    // DB-only pass (no OpenLibrary): fold every manual placeholder that duplicates a
    // real OL book already under the same author (same normalized title) into that
    // real row. The series builder mints these placeholders when it can't match a
    // catalogue title to an existing book, so they pile up as duplicates that the
    // OL-search path is far too slow (capped, rate-limited) to ever clear. Here we
    // already hold the canonical row, so no web call is needed.
    private async Task<int> MergeManualDuplicatesIntoRealBooksAsync(LibraryDbContext db, CancellationToken ct)
    {
        _currentMessage = "Merging manual duplicates of existing books (no OL needed)";

        var manualIds = await db.Books
            .Where(m => m.OpenLibraryWorkKey.StartsWith(ManualWorkKey.Prefix)
                     && m.NormalizedTitle != null && m.NormalizedTitle != ""
                     && db.Books.Any(o => o.AuthorId == m.AuthorId
                                       && o.NormalizedTitle == m.NormalizedTitle
                                       && o.Id != m.Id
                                       && !o.OpenLibraryWorkKey.StartsWith(ManualWorkKey.Prefix)))
            .OrderBy(m => m.Id)
            .Select(m => m.Id)
            .ToListAsync(ct);

        var merged = 0;
        for (var i = 0; i < manualIds.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if ((i & 255) == 0 && i > 0)
                _currentMessage = $"Merging manual duplicates of existing books… {i}/{manualIds.Count}";

            var manual = await db.Books.FirstOrDefaultAsync(b => b.Id == manualIds[i], ct);
            if (manual is null || !ManualWorkKey.IsManual(manual.OpenLibraryWorkKey)
                || string.IsNullOrEmpty(manual.NormalizedTitle))
            { db.ChangeTracker.Clear(); continue; }

            // Canonical target: a non-manual same-title row under this author,
            // preferring one that actually has a file, then the oldest (lowest id).
            var target = await db.Books
                .Where(o => o.AuthorId == manual.AuthorId && o.NormalizedTitle == manual.NormalizedTitle
                         && o.Id != manual.Id && !o.OpenLibraryWorkKey.StartsWith(ManualWorkKey.Prefix))
                .OrderByDescending(o => o.LocalFiles.Any())
                .ThenBy(o => o.Id)
                .FirstOrDefaultAsync(ct);
            if (target is null) { db.ChangeTracker.Clear(); continue; }

            try
            {
                await MergeIntoAsync(db, manual, target, ct);
                await db.SaveChangesAsync(ct);
                merged++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Promote-manual-books: DB-merge of \"{Title}\" (id {Id}) failed", manual.Title, manual.Id);
            }
            finally { db.ChangeTracker.Clear(); }
        }

        if (merged > 0)
            _log.LogInformation("Promote-manual-books: DB-merged {Count} manual duplicate(s) into existing OL books with no OL search", merged);
        return merged;
    }

    // Searches OL for the book's title + author and returns the doc only when
    // both the title and the author agree. Title: normalized equality, or a
    // very close fuzzy match. Author: the doc must carry the author's OL key
    // (when the watchlist row has one) or a name that normalizes to the same.
    private static async Task<WorkSearchDoc?> FindOpenLibraryWorkAsync(
        OpenLibraryClient ol, Book book, CancellationToken ct)
    {
        var search = await ol.SearchWorksAsync(book.Title, book.Author.Name, ct);
        if (search?.Docs is not { Count: > 0 } docs) return null;

        var wantTitle = book.NormalizedTitle ?? TitleNormalizer.Normalize(book.Title);
        var wantAuthorKey = book.Author.OpenLibraryKey?.Trim();
        var wantAuthorName = TitleNormalizer.NormalizeAuthor(book.Author.Name);

        WorkSearchDoc? best = null;
        var bestScore = TitleFuzzyFloor;
        foreach (var doc in docs)
        {
            if (string.IsNullOrWhiteSpace(doc.Key) || string.IsNullOrWhiteSpace(doc.Title)) continue;

            var authorOk = !string.IsNullOrEmpty(wantAuthorKey)
                ? doc.AuthorKeys?.Any(k => string.Equals(
                    k.Split('/').Last(), wantAuthorKey, StringComparison.OrdinalIgnoreCase)) == true
                : doc.AuthorNames?.Any(n => TitleNormalizer.NormalizeAuthor(n) == wantAuthorName) == true;
            if (!authorOk) continue;

            var docTitle = TitleNormalizer.Normalize(doc.Title);
            if (docTitle == wantTitle) return doc; // exact — take it immediately
            var score = FuzzyScore.JaroWinkler(wantTitle, docTitle);
            if (score > bestScore) { bestScore = score; best = doc; }
        }
        return best;
    }

    // Folds the manual row's user-set data into the author's existing row for
    // the same OL work, repoints every file link, then deletes the manual row.
    // Nothing the user set is lost: series/position fill blanks on the target,
    // ownership/read-status/wanted carry over, files follow.
    private static async Task MergeIntoAsync(LibraryDbContext db, Book manual, Book target, CancellationToken ct)
    {
        if (target.SeriesId is null && manual.SeriesId is not null)
        {
            target.SeriesId = manual.SeriesId;
            target.SeriesPosition ??= manual.SeriesPosition;
        }
        else if (target.SeriesId == manual.SeriesId && target.SeriesPosition is null)
        {
            target.SeriesPosition = manual.SeriesPosition;
        }
        if (manual.ManuallyOwned && !target.ManuallyOwned)
        {
            target.ManuallyOwned = true;
            target.ManuallyOwnedAt = manual.ManuallyOwnedAt ?? DateTime.UtcNow;
        }
        if (manual.OwnedDifferentEdition && !target.OwnedDifferentEdition)
            target.OwnedDifferentEdition = true;
        if (target.ReadStatus == ReadStatus.Unread && manual.ReadStatus != ReadStatus.Unread)
        {
            target.ReadStatus = manual.ReadStatus;
            target.ReadAt = manual.ReadAt;
        }
        if (manual.Wanted) target.Wanted = true;
        target.CoverUrl ??= manual.CoverUrl;
        target.Isbn ??= manual.Isbn;

        // Primary file links follow the merge.
        var files = await db.LocalBookFiles.Where(f => f.BookId == manual.Id).ToListAsync(ct);
        foreach (var f in files) f.BookId = target.Id;

        // Omnibus references (CSV of extra book ids) get rewritten too. The SQL
        // Contains is a coarse prefilter ("12" also matches "112"); the precise
        // token check happens here.
        var manualIdText = manual.Id.ToString();
        var withAdditional = await db.LocalBookFiles
            .Where(f => f.AdditionalBookIds != null && f.AdditionalBookIds.Contains(manualIdText))
            .ToListAsync(ct);
        foreach (var f in withAdditional)
        {
            var ids = f.AdditionalBookIds!
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s == manualIdText ? target.Id.ToString() : s)
                .Distinct()
                .Where(s => s != (f.BookId == target.Id ? target.Id.ToString() : null))
                .ToList();
            f.AdditionalBookIds = ids.Count > 0 ? string.Join(',', ids) : null;
        }

        db.Books.Remove(manual);
    }
}

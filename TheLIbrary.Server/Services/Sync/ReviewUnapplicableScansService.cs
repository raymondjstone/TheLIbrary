using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

public sealed record UnapplicableReviewSummary(int Reviewed, int Remaining);

// Scheduled background job: marks content-scan rows as reviewed when neither their
// ISBN nor their title guess resolves to any of the author's known books. These
// rows can't be auto-applied and serve no purpose on the Identified page, so
// they're silently dismissed to avoid UI clutter. Uses the exact same title
// matcher as ApplyContentGuessCoreAsync (SyncService.FindBestKnownBookAsync) and
// only cached ISBN lookups (no live OpenLibrary calls), capped per run for large
// backlogs.
public sealed class ReviewUnapplicableScansService
{
    public const int MaxPerRun = 500;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly ILogger<ReviewUnapplicableScansService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;
    private UnapplicableReviewSummary? _lastResult;

    public ReviewUnapplicableScansService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        ILogger<ReviewUnapplicableScansService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;
    public UnapplicableReviewSummary? LastResult => _lastResult;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("review-unapplicable-scans", out var holder))
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
            catch (Exception ex) { _log.LogError(ex, "Review unapplicable scans job failed"); }
            finally { _isRunning = false; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    internal Task<UnapplicableReviewSummary> RunForTestsAsync(CancellationToken ct) => RunAsync(ct);

    // Only tracked scans (files already in an author folder) with a title guess
    // that haven't been reviewed yet and are still unmatched — shared between the
    // batch query and the post-run remaining-count so the two can't drift apart.
    private static IQueryable<BookContentScan> EligibleScans(LibraryDbContext db) =>
        db.BookContentScans.Where(c => !c.Reviewed
            && c.Source != "untracked"
            && c.Title != null
            && c.AuthorId != null
            && db.LocalBookFiles.Any(f => f.FullPath == c.FullPath && f.BookId == null));

    private async Task<UnapplicableReviewSummary> RunAsync(CancellationToken ct)
    {
        _currentMessage = "Finding scan rows that can't be auto-applied";
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<LibraryDbContext>();

        var maxPerRun = await JobRunLimits.GetAsync(db, AppSettingKeys.ReviewUnapplicableScansMaxPerRun, MaxPerRun, ct);

        _log.LogInformation("Review unapplicable scans: looking for candidates (max {Max})", maxPerRun);

        var candidates = await EligibleScans(db)
            .OrderBy(c => c.Id)
            .Take(maxPerRun)
            .ToListAsync(ct);

        _log.LogInformation("Review unapplicable scans: found {Count} candidates", candidates.Count);
        _currentMessage = $"Found {candidates.Count} candidates to check";

        int reviewed = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var scan = candidates[i];
            _currentMessage = $"Checking scan {i + 1}/{candidates.Count}";

            var file = await db.LocalBookFiles.FirstOrDefaultAsync(f => f.FullPath == scan.FullPath, ct);
            if (file is null || file.BookId is not null || file.AuthorId is null)
            {
                scan.Reviewed = true; // file state changed, mark as reviewed anyway
                ActivityLogger.Record(db, "Scan auto-dismissed", 
                    $"Title guess '{scan.Title}' dismissed (file state changed): {Path.GetFileName(scan.FullPath)}", 
                    source: "review-unapplicable-scans");
                reviewed++;
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                continue;
            }

            var author = await db.Authors.FirstOrDefaultAsync(a => a.Id == file.AuthorId, ct);
            if (author is null)
            {
                scan.Reviewed = true;
                ActivityLogger.Record(db, "Scan auto-dismissed", 
                    $"Title guess '{scan.Title}' dismissed (author not found): {Path.GetFileName(scan.FullPath)}", 
                    source: "review-unapplicable-scans");
                reviewed++;
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                continue;
            }

            // ISBN is definitive and takes priority over title, same as
            // ApplyContentGuessCoreAsync. Only a cached lookup is consulted here (no
            // live OpenLibrary call) — an ISBN with no cached resolution yet hasn't
            // had its chance to resolve (IsbnResolutionCatchupService/IsbnMissRetryService
            // haven't gotten to it), so it's left unreviewed rather than dismissed.
            var isbnKey = IsbnResolution.IsbnKey(scan.Isbn);
            if (isbnKey is not null)
            {
                var resolution = await db.IsbnResolutions.AsNoTracking().FirstOrDefaultAsync(r => r.Isbn == isbnKey, ct);
                if (resolution is null)
                {
                    continue; // not yet attempted — wait for the resolver, don't dismiss
                }
                if (!string.IsNullOrWhiteSpace(resolution.WorkKey))
                {
                    var doc = new WorkSearchDoc
                    {
                        AuthorNames = resolution.AuthorName is null ? null : new() { resolution.AuthorName },
                        AuthorKeys = resolution.AuthorKey is null ? null : new() { resolution.AuthorKey },
                    };
                    if (IsbnAuthorAgreement.Matches(doc, author))
                    {
                        continue; // resolves to one of this author's works — leave for apply-all
                    }
                }
                // else: confirmed miss, or resolved to a different author — fall through
                // to the title-based match, same as ApplyContentGuessCoreAsync's fallback.
            }

            // Try to find a matching book using the exact same logic as
            // ApplyContentGuessCoreAsync's title fallback (shared, not reimplemented,
            // so the two paths can't silently diverge).
            var known = await SyncService.FindBestKnownBookAsync(db, author, scan.Title!, scan.SeriesPosition, ct);
            if (known is not null)
            {
                continue; // matches a known book — leave for apply-all to link it
            }

            // Neither ISBN nor title resolves to one of the author's known books —
            // this row can't be auto-applied, so it's dismissed.
            scan.Reviewed = true;
            ActivityLogger.Record(db, "Scan auto-dismissed",
                $"Title guess '{scan.Title}' for {author.Name} doesn't match any known book: {Path.GetFileName(scan.FullPath)}",
                source: "review-unapplicable-scans");
            reviewed++;

            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        var remaining = await EligibleScans(db).CountAsync(ct);

        _currentMessage = $"Done — {reviewed} marked as reviewed, {remaining} remaining";
        _log.LogInformation("Review unapplicable scans: {Reviewed} marked, {Remaining} remaining", reviewed, remaining);

        return new UnapplicableReviewSummary(reviewed, remaining);
    }
}

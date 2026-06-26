using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

public sealed record UntrackedAuthorAssignmentSummary(int Assigned, int Skipped, int Failed, int Remaining);

// Scheduled job behind the Identified page's "Assign all untracked books to
// authors" bulk action: files every untracked content-scan row under its
// OL-resolved (or guessed) author, creating authors as needed, via
// UntrackedAuthorAssigner. Capped per run because OpenLibrary is rate-limited;
// the recurring schedule (every 15 minutes by default) works through the
// backlog. Rows that carry a series catalogue are kept (tagged with their new
// author) so their series can then be built. Singleton through
// BackgroundTaskCoordinator so it can't overlap with sync / incoming / organize.
public sealed class UntrackedAuthorAssignmentService
{
    public const int MaxPerRun = 1000; // OpenLibrary is rate-limited; keep one run bounded.

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly ILogger<UntrackedAuthorAssignmentService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;
    private UntrackedAuthorAssignmentSummary? _lastResult;

    public UntrackedAuthorAssignmentService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        ILogger<UntrackedAuthorAssignmentService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;
    public UntrackedAuthorAssignmentSummary? LastResult => _lastResult;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("add authors from OL", out var holder))
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
            catch (Exception ex) { _log.LogError(ex, "Assign-authors job failed"); }
            finally { _isRunning = false; _currentMessage = null; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    internal Task<UntrackedAuthorAssignmentSummary> RunForTestsAsync(CancellationToken ct) => RunAsync(ct);

    private async Task<UntrackedAuthorAssignmentSummary> RunAsync(CancellationToken ct)
    {
        _log.LogInformation("Assign-authors job starting");
        _currentMessage = "Loading untracked books to file";
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var assigner = scope.ServiceProvider.GetRequiredService<UntrackedAuthorAssigner>();

        // Candidate set: untracked guesses not yet filed AND not already attempted.
        // AssignAttemptedAt is the durable "we tried and couldn't resolve this"
        // marker — without it the job re-queried OpenLibrary for the same wall of
        // unresolvable rows every 15 minutes (worse: the old skip-list was
        // in-memory, so a restart wiped it and the whole backlog was retried from
        // scratch). Attempted rows stay skipped until the user resets the flag from
        // the Settings page (e.g. after adding authors that might now match).
        var candidateIds = await db.BookContentScans
            .Where(c => !c.Reviewed && c.AuthorId == null && c.AssignAttemptedAt == null
                && (c.Isbn != null || c.Author != null || c.Title != null))
            .OrderBy(c => c.Id)
            .Select(c => c.Id)
            .ToListAsync(ct);

        var maxPerRun = await JobRunLimits.GetAsync(db, AppSettingKeys.AssignAuthorsMaxPerRun, MaxPerRun, ct);
        var toAttempt = candidateIds.Take(maxPerRun).ToList();

        int assigned = 0, skipped = 0, failed = 0;
        for (var i = 0; i < toAttempt.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var scan = await db.BookContentScans.FirstOrDefaultAsync(c => c.Id == toAttempt[i], ct);
            if (scan is null) continue;
            _currentMessage = $"Filing {i + 1}/{toAttempt.Count}: {Path.GetFileName(scan.FullPath)}"
                + $" ({assigned} filed, {skipped} skipped)";
            try
            {
                var r = await assigner.AssignAsync(scan, ct);
                if (r.Assigned)
                {
                    assigned++;
                }
                else
                {
                    skipped++;
                    // Durably mark it attempted so later runs skip it instead of
                    // re-querying OpenLibrary for the same unresolvable row. The scan
                    // is tracked and the skip paths add no other pending changes, so
                    // a plain SaveChanges persists just this flag.
                    scan.AssignAttemptedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    _log.LogDebug("Assign-authors: skipped {Path}: {Reason}", scan.FullPath, r.Reason);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                failed++;
                db.ChangeTracker.Clear(); // drop the failed pending change, keep going
                _log.LogWarning(ex, "Assign-authors: failed on {Path}", scan.FullPath);
            }
        }

        var remaining = Math.Max(0, candidateIds.Count - assigned);
        var summary = new UntrackedAuthorAssignmentSummary(assigned, skipped, failed, remaining);
        _log.LogInformation(
            "Assign-authors job done — filed {Assigned}, skipped {Skipped}, failed {Failed}, remaining {Remaining}",
            assigned, skipped, failed, remaining);
        _currentMessage = $"Done — {assigned} filed, {skipped} skipped, {failed} failed, {remaining} remaining";
        return summary;
    }
}

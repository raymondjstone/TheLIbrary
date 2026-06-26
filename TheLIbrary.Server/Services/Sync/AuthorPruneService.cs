using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

public sealed record AuthorPruneSummary(int Deleted);

// Scheduled job (OFF by default): removes empty auto-created authors — the
// homonym/guess noise that jobs like same-name-authors, assign-authors,
// content-scan and adopt-unknown-authors generate. Deliberately conservative:
// it ONLY touches rows it can prove are throwaway, and never a user-added,
// restored, starred, linked, annotated, or file/book-bearing author.
public sealed class AuthorPruneService
{
    // Sources whose empty rows are safe to drop. Manual/restore/null (pre-existing)
    // are intentionally excluded.
    private static readonly string[] PrunableSources = { "same-name", "content-scan", "assign", "adopt" };

    public const int MaxPerRun = 5000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly ILogger<AuthorPruneService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;
    private AuthorPruneSummary? _lastResult;

    public AuthorPruneService(IServiceScopeFactory scopeFactory, BackgroundTaskCoordinator coordinator, ILogger<AuthorPruneService> log)
    {
        _scopeFactory = scopeFactory; _coordinator = coordinator; _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;
    public AuthorPruneSummary? LastResult => _lastResult;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("prune empty authors", out var holder))
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
            catch (Exception ex) { _log.LogError(ex, "Author prune job failed"); }
            finally { _isRunning = false; _currentMessage = null; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    internal Task<AuthorPruneSummary> RunForTestsAsync(CancellationToken ct) => RunAsync(ct);

    private async Task<AuthorPruneSummary> RunAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        _currentMessage = "Finding empty auto-created authors";

        var candidates = db.Authors.Where(a =>
            a.CreationSource != null && PrunableSources.Contains(a.CreationSource)
            && (a.Status == AuthorStatus.Pending || a.Status == AuthorStatus.NotFound)
            && a.Priority == 0
            && a.LinkedToAuthorId == null
            && !a.NotifyOnNewBooks
            && (a.Notes == null || a.Notes == "")
            && !db.Books.Any(b => b.AuthorId == a.Id)
            && !db.LocalBookFiles.Any(f => f.AuthorId == a.Id)
            && !db.Authors.Any(x => x.LinkedToAuthorId == a.Id));   // nobody links to it

        var maxPerRun = await JobRunLimits.GetAsync(db, AppSettingKeys.PruneAuthorsMaxPerRun, MaxPerRun, ct);
        var ids = await candidates.OrderBy(a => a.Id).Take(maxPerRun).Select(a => a.Id).ToListAsync(ct);
        if (ids.Count == 0)
        {
            _currentMessage = "Nothing to prune";
            return new AuthorPruneSummary(0);
        }

        _currentMessage = $"Deleting {ids.Count} empty author(s)";
        var deleted = await db.Authors.Where(a => ids.Contains(a.Id)).ExecuteDeleteAsync(ct);
        _log.LogInformation("Author prune: removed {Count} empty auto-created authors", deleted);
        return new AuthorPruneSummary(deleted);
    }
}

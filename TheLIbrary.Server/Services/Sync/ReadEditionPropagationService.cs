using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

public sealed record ReadEditionPropagationSummary(int BooksMarked);

// Scheduled job: where the same author has several catalogue entries for the
// same title (NormalizedTitle) and AT LEAST ONE of them is marked Read, mark
// every other edition Read too — reading one edition means you've read the work,
// regardless of which catalogue row carried the file. Idempotent: editions
// already Read are left untouched (their ReadAt is preserved).
//
// Pure DB work — a single set-based UPDATE, no disk I/O on the NAS mount.
public sealed class ReadEditionPropagationService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly ILogger<ReadEditionPropagationService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;
    private ReadEditionPropagationSummary? _lastResult;

    public ReadEditionPropagationService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        ILogger<ReadEditionPropagationService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;
    public ReadEditionPropagationSummary? LastResult => _lastResult;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("propagate read status across editions", out var holder))
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
            catch (Exception ex) { _log.LogError(ex, "Mark-editions-read job failed"); }
            finally { _isRunning = false; _currentMessage = null; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    // Pure selection logic, mirroring the SQL in RunAsync, exposed for unit tests:
    // an edition is marked when it isn't already Read, has a real title, and a
    // DIFFERENT edition with the same (author, normalized title) IS Read. Entries
    // with no normalized title are skipped — grouping them would wrongly lump
    // every untitled book of an author together.
    internal static IReadOnlyList<int> SelectIdsToMark(
        IReadOnlyList<(int Id, int AuthorId, string? NormalizedTitle, bool IsRead)> books)
    {
        var readKeys = books
            .Where(b => b.IsRead && !string.IsNullOrEmpty(b.NormalizedTitle))
            .Select(b => (b.AuthorId, b.NormalizedTitle))
            .ToHashSet();

        return books
            .Where(b => !b.IsRead
                     && !string.IsNullOrEmpty(b.NormalizedTitle)
                     && readKeys.Contains((b.AuthorId, b.NormalizedTitle)))
            .Select(b => b.Id)
            .OrderBy(id => id)
            .ToList();
    }

    private async Task<ReadEditionPropagationSummary> RunAsync(CancellationToken ct)
    {
        _log.LogInformation("Mark-editions-read job starting");
        _currentMessage = "Scanning read editions";
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

        // Mark Read every not-yet-Read edition whose (author, normalized title) is
        // also held by a DIFFERENT edition that is already Read. Fill ReadAt where
        // it's missing (preserve any date already on the row). One UPDATE.
        var now = DateTime.UtcNow;
        var marked = await db.Books
            .Where(b => b.NormalizedTitle != null && b.NormalizedTitle != ""
                     && b.ReadStatus != ReadStatus.Read
                     && db.Books.Any(s => s.AuthorId == b.AuthorId
                                       && s.NormalizedTitle == b.NormalizedTitle
                                       && s.Id != b.Id
                                       && s.ReadStatus == ReadStatus.Read))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.ReadStatus, ReadStatus.Read)
                .SetProperty(b => b.ReadAt, b => b.ReadAt ?? now), ct);

        _log.LogInformation("Mark-editions-read job marked {Count} edition(s) Read", marked);
        _currentMessage = $"Done — marked {marked} edition(s)";
        return new ReadEditionPropagationSummary(marked);
    }
}

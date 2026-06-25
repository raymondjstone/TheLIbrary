using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

public sealed record OtherEditionMarkSummary(int BooksMarked);

// Scheduled job: where the same author has multiple catalogue entries for the
// same title (NormalizedTitle) and AT LEAST ONE of them has an ebook file
// linked, every sibling entry that has NO file of its own is marked
// "Owned (other edition)" (OwnedDifferentEdition) — you own the work, just a
// different edition than that catalogue row — so the duplicate entries drop off
// the Missing / Wanted lists. Idempotent: rows already flagged are left alone.
//
// "Has an ebook" is LocalFiles.Any() — the same predicate the rest of the app
// uses to decide a book is owned-via-ebook — so the job is a single set-based
// UPDATE with no disk I/O on the NAS mount.
public sealed class OtherEditionMarkerService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly ILogger<OtherEditionMarkerService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;
    private OtherEditionMarkSummary? _lastResult;

    public OtherEditionMarkerService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        ILogger<OtherEditionMarkerService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;
    public OtherEditionMarkSummary? LastResult => _lastResult;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("mark other-edition duplicates", out var holder))
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
            catch (Exception ex) { _log.LogError(ex, "Mark-other-editions job failed"); }
            finally { _isRunning = false; _currentMessage = null; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    // Pure selection logic, mirroring the SQL in RunAsync, exposed for unit tests:
    // an entry is marked when it has no file, isn't already flagged, has a real
    // title, and a DIFFERENT entry with the same (author, normalized title) has a
    // file. Entries with no normalized title are skipped — grouping them would
    // wrongly lump every untitled book of an author together.
    internal static IReadOnlyList<int> SelectIdsToMark(
        IReadOnlyList<(int Id, int AuthorId, string? NormalizedTitle, bool HasFile, bool OwnedDifferentEdition)> books)
    {
        var ebookBackedKeys = books
            .Where(b => b.HasFile && !string.IsNullOrEmpty(b.NormalizedTitle))
            .Select(b => (b.AuthorId, b.NormalizedTitle))
            .ToHashSet();

        return books
            .Where(b => !b.HasFile
                     && !b.OwnedDifferentEdition
                     && !string.IsNullOrEmpty(b.NormalizedTitle)
                     && ebookBackedKeys.Contains((b.AuthorId, b.NormalizedTitle)))
            .Select(b => b.Id)
            .OrderBy(id => id)
            .ToList();
    }

    private async Task<OtherEditionMarkSummary> RunAsync(CancellationToken ct)
    {
        _log.LogInformation("Mark-other-editions job starting");
        _currentMessage = "Scanning duplicate titles";
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

        // Flag every fileless, not-yet-flagged entry whose (author, normalized
        // title) is also held by a DIFFERENT entry that has a file. One UPDATE,
        // no per-row work.
        var marked = await db.Books
            .Where(b => b.NormalizedTitle != null && b.NormalizedTitle != ""
                     && !b.OwnedDifferentEdition
                     && !b.LocalFiles.Any()
                     && db.Books.Any(s => s.AuthorId == b.AuthorId
                                       && s.NormalizedTitle == b.NormalizedTitle
                                       && s.Id != b.Id
                                       && s.LocalFiles.Any()))
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.OwnedDifferentEdition, true), ct);

        _log.LogInformation("Mark-other-editions job flagged {Count} duplicate entry/entries as other-edition", marked);
        _currentMessage = $"Done — marked {marked} book(s)";
        return new OtherEditionMarkSummary(marked);
    }
}

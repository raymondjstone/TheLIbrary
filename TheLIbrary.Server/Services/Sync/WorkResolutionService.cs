using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.IO;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

public sealed record WorkResolutionSummary(int Considered, int Linked, int Skipped);

// Scheduled job (OFF by default): link files that already know their AUTHOR but
// not their WORK, using the ISBN we already extracted. The title-only matcher
// never consults ISBN, and the ISBN-aware assigner skips author-linked files —
// so ~36k files carry a good ISBN that nothing ever resolves. This closes that
// gap: ISBN → OpenLibrary work → Book under the file's existing author → BookId.
// Candidate selection is DB-only (no NAS reads); each resolution is one or two
// OpenLibrary calls, paced by the shared rate limiter and capped per run.
public sealed class WorkResolutionService
{
    public const int MaxPerRun = 200;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly ILogger<WorkResolutionService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;
    private WorkResolutionSummary? _lastResult;

    public WorkResolutionService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        ILogger<WorkResolutionService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;
    public WorkResolutionSummary? LastResult => _lastResult;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("resolve-works", out var holder))
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
            catch (Exception ex) { _log.LogError(ex, "Work resolution failed"); }
            finally { _isRunning = false; _currentMessage = null; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    internal Task<WorkResolutionSummary> RunForTestsAsync(CancellationToken ct) => RunAsync(ct);

    private async Task<WorkResolutionSummary> RunAsync(CancellationToken ct)
    {
        _currentMessage = "Finding author-linked files with an ISBN but no work";
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<LibraryDbContext>();
        var assigner = new UntrackedAuthorAssigner(
            db, sp.GetRequiredService<OpenLibraryClient>(), sp.GetRequiredService<IFileSystem>());

        var archiveLeaf = await ArchivePolicy.LoadLeafAsync(db, ct);
        var maxPerRun = await JobRunLimits.GetAsync(db, AppSettingKeys.ResolveWorksMaxPerRun, MaxPerRun, ct);

        // Author-linked, work-less files for which we extracted an ISBN. DB-only
        // selection (join the scan row by path) — no disk access on the NAS mount.
        var candidates = await db.LocalBookFiles.AsNoTracking()
            .Where(f => f.BookId == null && f.AuthorId != null)
            .Where(ArchivePolicy.NotUnder(archiveLeaf))
            .Join(db.BookContentScans.AsNoTracking(),
                  f => f.FullPath, s => s.FullPath,
                  (f, s) => new { f.Id, s.Isbn, s.Title })
            .Where(x => x.Isbn != null && x.Isbn != "")
            .OrderBy(x => x.Id)
            .Take(maxPerRun)
            .ToListAsync(ct);

        int considered = 0, linked = 0, skipped = 0;
        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();
            considered++;
            _currentMessage = $"Resolving work {considered}/{candidates.Count}";

            var file = await db.LocalBookFiles.FirstOrDefaultAsync(f => f.Id == c.Id, ct);
            if (file is null || file.BookId != null) continue; // raced/changed since selection

            if (await assigner.TryLinkWorkByIsbnAsync(file, c.Isbn, c.Title, ct))
            {
                await db.SaveChangesAsync(ct);
                linked++;
            }
            else skipped++;
        }

        if (linked > 0)
        {
            Services.ActivityLogger.Record(db, "resolve-works",
                $"Linked {linked} file(s) to their OpenLibrary work by ISBN (of {considered} considered)",
                source: "resolve-works");
            await db.SaveChangesAsync(ct);
        }

        _log.LogInformation("Work resolution: considered {Considered}, linked {Linked}, skipped {Skipped}",
            considered, linked, skipped);
        _currentMessage = $"Done — linked {linked} of {considered}";
        return new WorkResolutionSummary(considered, linked, skipped);
    }
}

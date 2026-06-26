using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.Scheduling;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Services.Download;

public sealed record AutoReplaceDamagedSummary(int Attempted, int Grabbed, bool Configured);

// Scheduled job (OFF by default): for books whose only/while-damaged copies failed
// the integrity check, search the configured indexer and send the best NZB to
// SABnzbd — the automated equivalent of clicking "Grab" on each damaged book.
// Capped per run because the indexer is rate-limited; no-ops (and logs) when
// download automation isn't configured. Off by default since it pulls downloads.
public sealed class AutoReplaceDamagedService
{
    public const int MaxPerRun = 20;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly ILogger<AutoReplaceDamagedService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;
    private AutoReplaceDamagedSummary? _lastResult;

    public AutoReplaceDamagedService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        ILogger<AutoReplaceDamagedService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;
    public AutoReplaceDamagedSummary? LastResult => _lastResult;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("auto-replace-damaged", out var holder))
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
            catch (Exception ex) { _log.LogError(ex, "Auto-replace-damaged failed"); }
            finally { _isRunning = false; _currentMessage = null; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    internal Task<AutoReplaceDamagedSummary> RunForTestsAsync(CancellationToken ct) => RunAsync(ct);

    private async Task<AutoReplaceDamagedSummary> RunAsync(CancellationToken ct)
    {
        _currentMessage = "Checking download automation";
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        // Resolve the (scoped) grab service from the scope — this service is a
        // singleton, so it must not capture a scoped dependency via the ctor.
        var grab = scope.ServiceProvider.GetRequiredService<NzbGrabService>();

        var cfg = await grab.GetConfigAsync(db, ct);
        if (!cfg.Ready)
        {
            _log.LogInformation("Auto-replace-damaged: download automation not configured — skipping");
            _currentMessage = "Skipped — download automation not configured";
            return new AutoReplaceDamagedSummary(0, 0, false);
        }

        var archiveLeaf = await ArchivePolicy.LoadLeafAsync(db, ct);
        var damaged = await db.LocalBookFiles.AsNoTracking()
            .Where(f => f.IntegrityOk == false && f.BookId != null)
            .Where(ArchivePolicy.NotUnder(archiveLeaf))
            .Select(f => new { f.BookId, f.FullPath })
            .ToListAsync(ct);

        var maxPerRun = await JobRunLimits.GetAsync(db, AppSettingKeys.AutoReplaceDamagedMaxPerRun, MaxPerRun, ct);
        var bookIds = damaged
            .Where(x => BookIntegrityChecker.IsEbook(x.FullPath))
            .Select(x => x.BookId!.Value)
            .Distinct()
            .Take(maxPerRun)
            .ToList();

        int attempted = 0, grabbed = 0;
        foreach (var bookId in bookIds)
        {
            ct.ThrowIfCancellationRequested();
            attempted++;
            _currentMessage = $"Grabbing replacement {attempted}/{bookIds.Count}";
            var r = await grab.GrabAsync(bookId, ct);
            if (r.Success) grabbed++;
        }

        _log.LogInformation("Auto-replace-damaged: attempted {Attempted}, grabbed {Grabbed}", attempted, grabbed);
        _currentMessage = $"Done — grabbed {grabbed}/{attempted} replacement(s)";
        return new AutoReplaceDamagedSummary(attempted, grabbed, true);
    }
}

using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

public sealed record BookIntegritySummary(int Checked, int Ok, int Damaged, int Skipped);

// Scheduled job: opens (or converts via Calibre) each matched ebook file and
// verifies it has at least BookIntegrityChecker.MinPages pages. Files that
// can't be opened/converted, or are too short, are flagged damaged and surface
// on the Damaged page.
//
// Incremental + cheap to re-run: a file is only (re)checked when its folder
// fingerprint (LocalBookFile.SizeBytes) differs from the value stored at the
// last check, or it has never been checked. That comparison is DB-only, so the
// candidate scan does no disk I/O — each run then processes at most
// MaxBooksPerRun files, working through the backlog over successive runs.
public sealed class BookIntegrityService
{
    public const int DefaultMaxBooksPerRun = 200;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly BookIntegrityChecker _checker;
    private readonly ILogger<BookIntegrityService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;
    private BookIntegritySummary? _lastResult;

    public BookIntegrityService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        BookIntegrityChecker checker,
        ILogger<BookIntegrityService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _checker = checker;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;
    public BookIntegritySummary? LastResult => _lastResult;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("check book integrity", out var holder))
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
            catch (Exception ex) { _log.LogError(ex, "Book-integrity job failed"); }
            finally { _isRunning = false; _currentMessage = null; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    // Test seam: runs the body synchronously (no coordinator / background task)
    // so assertions can inspect the resulting DB state deterministically.
    internal Task<BookIntegritySummary> RunForTestsAsync(CancellationToken ct) => RunAsync(ct);

    private async Task<BookIntegritySummary> RunAsync(CancellationToken ct)
    {
        _log.LogInformation("Book-integrity job starting");
        _currentMessage = "Loading files to check";
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

        var max = await LoadMaxBooksPerRunAsync(db, ct);

        // Candidate = matched ebook file whose fingerprint changed since (or was
        // never) checked. The ebook-extension filter and the size comparison run
        // in SQL so we never materialise the whole LocalBookFiles table.
        // Starred-author books (Author.Priority >= 1) are checked first, matching
        // how every other job prioritises the user's flagged authors; ties fall
        // back to Id so progress through the backlog stays deterministic.
        var candidates = await db.LocalBookFiles
            .Where(f => f.BookId != null
                && (f.IntegrityCheckedSize == null || f.IntegrityCheckedSize != f.SizeBytes)
                && (f.FullPath.EndsWith(".epub") || f.FullPath.EndsWith(".pdf")
                    || f.FullPath.EndsWith(".mobi") || f.FullPath.EndsWith(".azw")
                    || f.FullPath.EndsWith(".azw3") || f.FullPath.EndsWith(".fb2")
                    || f.FullPath.EndsWith(".cbz") || f.FullPath.EndsWith(".cbr")
                    || f.FullPath.EndsWith(".lit") || f.FullPath.EndsWith(".djvu")
                    || f.FullPath.EndsWith(".doc") || f.FullPath.EndsWith(".docx")
                    || f.FullPath.EndsWith(".rtf") || f.FullPath.EndsWith(".txt")))
            .OrderByDescending(f => f.Book!.Author.Priority)
            .ThenBy(f => f.Id)
            .Take(max)
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            _log.LogInformation("Book-integrity job found nothing new to check");
            _currentMessage = "Done — nothing to check";
            return new BookIntegritySummary(0, 0, 0, 0);
        }

        int checkedCount = 0, ok = 0, damaged = 0, skipped = 0;

        for (var i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = candidates[i];
            _currentMessage = $"Checking {i + 1}/{candidates.Count}";

            var result = await _checker.CheckAsync(file.FullPath, ct);
            if (result.Status == IntegrityStatus.Skipped)
            {
                // Leave the record untouched so it's retried once the blocker
                // (e.g. Calibre not configured) is resolved.
                skipped++;
                continue;
            }

            file.IntegrityOk = result.Status == IntegrityStatus.Ok;
            file.IntegrityError = result.Status == IntegrityStatus.Ok ? null : result.Error;
            file.IntegrityPages = result.Pages;
            file.IntegrityCheckedSize = file.SizeBytes;
            file.IntegrityCheckedAt = DateTime.UtcNow;

            checkedCount++;
            if (result.Status == IntegrityStatus.Ok) ok++; else damaged++;

            // Persist incrementally so a long, cancelled run keeps its progress.
            if ((i + 1) % 25 == 0) await db.SaveChangesAsync(ct);
        }

        await db.SaveChangesAsync(ct);

        _log.LogInformation(
            "Book-integrity job done — checked {Checked}, ok {Ok}, damaged {Damaged}, skipped {Skipped}",
            checkedCount, ok, damaged, skipped);
        _currentMessage = $"Done — checked {checkedCount}, damaged {damaged}";
        return new BookIntegritySummary(checkedCount, ok, damaged, skipped);
    }

    private static async Task<int> LoadMaxBooksPerRunAsync(LibraryDbContext db, CancellationToken ct)
    {
        var raw = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.IntegrityMaxBooksPerRun)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        return int.TryParse(raw, out var n) && n > 0 ? n : DefaultMaxBooksPerRun;
    }
}

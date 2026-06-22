using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.IO;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Services.Scheduling;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Services.Llm;

public sealed record LlmIdentificationSummary(int Considered, int Resolved, int Calls, bool Enabled);

// Scheduled job (OFF by default): last-resort identification for quarantined
// (__unknown) files that the deterministic + filename paths couldn't resolve.
// Sends the signals we already have to the configured LLM (Claude or ChatGPT),
// then feeds its guessed title/author through the SAME OpenLibrary validation +
// assignment as everything else — so a hallucinated guess is rejected, never
// filed. Cost is bounded two ways: a per-run cap and a hard rolling daily cap,
// and each file is marked LlmAttemptedAt so a hopeless file is never re-sent.
public sealed class LlmIdentificationService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly ILogger<LlmIdentificationService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;
    private LlmIdentificationSummary? _lastResult;

    public LlmIdentificationService(
        IServiceScopeFactory scopeFactory, BackgroundTaskCoordinator coordinator, ILogger<LlmIdentificationService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;
    public LlmIdentificationSummary? LastResult => _lastResult;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("llm-identify", out var holder))
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
            catch (Exception ex) { _log.LogError(ex, "LLM identification failed"); }
            finally { _isRunning = false; _currentMessage = null; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    internal Task<LlmIdentificationSummary> RunForTestsAsync(CancellationToken ct) => RunAsync(ct);

    private async Task<LlmIdentificationSummary> RunAsync(CancellationToken ct)
    {
        _currentMessage = "Checking LLM configuration";
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<LibraryDbContext>();

        var cfg = await LlmMetadataClient.LoadConfigAsync(db, ct);
        if (!cfg.Ready)
        {
            _currentMessage = "Skipped — LLM not enabled / no API key";
            return new LlmIdentificationSummary(0, 0, 0, false);
        }

        // Daily budget: reset the rolling counter when the day rolls over.
        var (usedToday, today) = await LoadUsageAsync(db, ct);
        var dailyRemaining = Math.Max(0, cfg.MaxPerDay - usedToday);
        var budget = Math.Min(cfg.MaxPerRun, dailyRemaining);
        if (budget <= 0)
        {
            _currentMessage = $"Skipped — daily cap reached ({usedToday}/{cfg.MaxPerDay})";
            return new LlmIdentificationSummary(0, 0, 0, true);
        }

        var client = sp.GetRequiredService<LlmMetadataClient>();
        var reader = sp.GetRequiredService<BookTextReader>();
        var fs = sp.GetRequiredService<IFileSystem>();
        var assigner = new UntrackedAuthorAssigner(db, sp.GetRequiredService<OpenLibraryClient>(), fs);

        // Opaque residual: quarantined files with no author the cheap paths could
        // find, not yet sent to the LLM.
        var candidateIds = await db.BookContentScans.AsNoTracking()
            .Where(s => s.Source == "untracked" && s.AuthorId == null && s.Author == null && s.LlmAttemptedAt == null)
            .OrderBy(s => s.Id)
            .Take(budget)
            .Select(s => s.Id)
            .ToListAsync(ct);

        int considered = 0, resolved = 0, calls = 0;
        foreach (var id in candidateIds)
        {
            ct.ThrowIfCancellationRequested();
            var scan = await db.BookContentScans.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (scan is null || scan.AuthorId != null || scan.LlmAttemptedAt != null) continue;
            considered++;
            _currentMessage = $"Identifying {considered}/{candidateIds.Count}";

            scan.LlmAttemptedAt = DateTime.UtcNow; // mark first — never re-spend on this file

            if (!fs.FileExists(scan.FullPath)) { await db.SaveChangesAsync(ct); continue; }

            var embedded = FileMetadataReader.TryRead(scan.FullPath);
            string front;
            try { front = await reader.ReadHeadAsync(scan.FullPath, 4000, ct); }
            catch { front = ""; }

            var signals = new LlmSignals(
                FileName: Path.GetFileName(scan.FullPath),
                EmbeddedTitle: embedded?.Title, EmbeddedAuthor: embedded?.Author,
                Isbn: scan.Isbn ?? embedded?.Isbn, FrontMatter: front);

            var guess = await client.IdentifyAsync(cfg, signals, ct);
            calls++;
            if (guess is not null)
            {
                // Persist whatever the LLM found onto the scan row FIRST, so the data
                // survives even if the OpenLibrary check below rejects it — the row
                // then shows on the Identified page for manual review rather than
                // being lost. (Existing extracted values aren't overwritten with null.)
                if (!string.IsNullOrWhiteSpace(guess.Title)) scan.Title = Cap(guess.Title!, 500);
                if (!string.IsNullOrWhiteSpace(guess.Author)) scan.Author = Cap(guess.Author!, 500);
                if (!string.IsNullOrWhiteSpace(guess.Isbn) && string.IsNullOrWhiteSpace(scan.Isbn)) scan.Isbn = Cap(guess.Isbn!, 20);
                if (!string.IsNullOrWhiteSpace(guess.Series)) scan.Series = Cap(guess.Series!, 500);
                if (!string.IsNullOrWhiteSpace(guess.SeriesPosition)) scan.SeriesPosition = Cap(guess.SeriesPosition!, 50);
                await db.SaveChangesAsync(ct);

                // Then let the normal assigner verify the guess against OpenLibrary
                // and file it. If it can't be confirmed the scan is left as-is — its
                // LLM-found title/author/ISBN stay on the Identified page.
                var hasSignal = !string.IsNullOrWhiteSpace(scan.Isbn)
                    || !string.IsNullOrWhiteSpace(scan.Author) || !string.IsNullOrWhiteSpace(scan.Title);
                if (hasSignal)
                {
                    var outcome = await assigner.AssignAsync(scan, ct);
                    if (outcome.Assigned) resolved++;
                }
            }
            else await db.SaveChangesAsync(ct);
        }

        await SaveUsageAsync(db, today, usedToday + calls, ct);
        if (resolved > 0)
            ActivityLogger.Record(db, "llm-identify",
                $"LLM ({cfg.Provider}) identified {resolved} of {considered} quarantined file(s) in {calls} call(s)",
                source: "llm-identify");
        await db.SaveChangesAsync(ct);

        _log.LogInformation("LLM identify: considered {Considered}, resolved {Resolved}, calls {Calls}", considered, resolved, calls);
        _currentMessage = $"Done — identified {resolved} of {considered} ({calls} call(s))";
        return new LlmIdentificationSummary(considered, resolved, calls, true);
    }

    private static string Cap(string s, int max) => s.Length <= max ? s : s[..max];

    private static async Task<(int Used, string Today)> LoadUsageAsync(LibraryDbContext db, CancellationToken ct)
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var rows = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.LlmUsageDate || s.Key == AppSettingKeys.LlmUsageCount)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var date = rows.GetValueOrDefault(AppSettingKeys.LlmUsageDate);
        var count = int.TryParse(rows.GetValueOrDefault(AppSettingKeys.LlmUsageCount), out var n) ? n : 0;
        return (date == today ? count : 0, today); // new day → counter resets
    }

    private static async Task SaveUsageAsync(LibraryDbContext db, string today, int count, CancellationToken ct)
    {
        await Upsert(db, AppSettingKeys.LlmUsageDate, today, ct);
        await Upsert(db, AppSettingKeys.LlmUsageCount, count.ToString(), ct);
    }

    private static async Task Upsert(LibraryDbContext db, string key, string value, CancellationToken ct)
    {
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else row.Value = value;
    }
}

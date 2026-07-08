using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.IO;
using TheLibrary.Server.Services.Scheduling;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Services.Llm;

public sealed record LlmTitleMatchSummary(int Considered, int Matched, int Calls, bool Enabled);

// Scheduled job (OFF by default): reads unmatched files under starred authors,
// uses the configured LLM (Claude or ChatGPT) to identify the title from the
// book's content, then matches the result against the author's existing Book
// catalog using normalized title comparison. Cost is bounded by shared daily/
// per-run limits with LlmIdentificationService, and each file is marked
// LlmTitleMatchAttemptedAt so failed attempts are never re-spent.
public sealed class LlmTitleMatchService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly ILogger<LlmTitleMatchService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;
    private LlmTitleMatchSummary? _lastResult;

    public LlmTitleMatchService(
        IServiceScopeFactory scopeFactory, BackgroundTaskCoordinator coordinator, ILogger<LlmTitleMatchService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;
    public LlmTitleMatchSummary? LastResult => _lastResult;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("llm-title-match", out var holder))
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
            catch (Exception ex) { _log.LogError(ex, "LLM title matching failed"); }
            // _currentMessage is left holding the "Done — …" / "Skipped — …" summary so
            // the Sync page shows the run's outcome instead of reverting to blank the
            // instant a run finishes.
            finally { _isRunning = false; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    internal Task<LlmTitleMatchSummary> RunForTestsAsync(CancellationToken ct) => RunAsync(ct);

    private async Task<LlmTitleMatchSummary> RunAsync(CancellationToken ct)
    {
        _currentMessage = "Checking LLM configuration";
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<LibraryDbContext>();

        var cfg = await LlmMetadataClient.LoadConfigAsync(db, ct);
        if (!cfg.Ready)
        {
            _currentMessage = "Skipped — LLM not enabled / no API key";
            return new LlmTitleMatchSummary(0, 0, 0, false);
        }

        // Share the daily budget with LlmIdentificationService — both consume
        // the same paid API. The budget resets when the day rolls over.
        var (usedToday, today) = await LoadUsageAsync(db, ct);
        var dailyRemaining = Math.Max(0, cfg.MaxPerDay - usedToday);
        var budget = Math.Min(cfg.MaxPerRun, dailyRemaining);
        if (budget <= 0)
        {
            _currentMessage = $"Skipped — daily cap reached ({usedToday}/{cfg.MaxPerDay})";
            return new LlmTitleMatchSummary(0, 0, 0, true);
        }

        var client = sp.GetRequiredService<LlmMetadataClient>();
        var reader = sp.GetRequiredService<BookTextReader>();
        var fs = sp.GetRequiredService<IFileSystem>();

        // Unmatched files (BookId == null) under starred authors that the LLM
        // hasn't tried yet. We deliberately include files that may have a title
        // extracted by ContentScanService — the LLM can still rescue cases where
        // the extraction missed or was wrong.
        var candidateIds = await db.LocalBookFiles.AsNoTracking()
            .Where(f => f.BookId == null 
                && f.AuthorId != null 
                && f.LlmTitleMatchAttemptedAt == null
                && db.Authors.Any(a => a.Id == f.AuthorId && a.Priority >= 1))
            .OrderBy(f => f.Id)
            .Take(budget)
            .Select(f => f.Id)
            .ToListAsync(ct);

        int considered = 0, matched = 0, calls = 0;
        foreach (var id in candidateIds)
        {
            ct.ThrowIfCancellationRequested();
            var file = await db.LocalBookFiles
                .Include(f => f.Author)
                .FirstOrDefaultAsync(f => f.Id == id, ct);
            if (file is null || file.BookId != null || file.AuthorId is null 
                || file.LlmTitleMatchAttemptedAt != null || file.Author is null) 
                continue;

            considered++;
            _currentMessage = $"Matching {considered}/{candidateIds.Count}";

            file.LlmTitleMatchAttemptedAt = DateTime.UtcNow; // mark first — never re-spend on this file

            if (!fs.FileExists(file.FullPath)) { await db.SaveChangesAsync(ct); continue; }

            var embedded = FileMetadataReader.TryRead(file.FullPath);
            string front;
            try { front = await reader.ReadHeadAsync(file.FullPath, 4000, ct); }
            catch { front = ""; }

            var signals = new LlmSignals(
                FileName: Path.GetFileName(file.FullPath),
                EmbeddedTitle: embedded?.Title ?? file.MetadataTitle,
                EmbeddedAuthor: embedded?.Author ?? file.MetadataAuthor,
                Isbn: file.Isbn ?? embedded?.Isbn,
                FrontMatter: front);

            var guess = await client.IdentifyAsync(cfg, signals, ct);
            calls++;

            if (guess is not null && !string.IsNullOrWhiteSpace(guess.Title))
            {
                // Try to match the LLM-identified title against the author's
                // existing books using normalized title comparison.
                var normalizedGuess = TitleNormalizer.Normalize(guess.Title);
                if (!string.IsNullOrWhiteSpace(normalizedGuess))
                {
                    var match = await db.Books
                        .Where(b => b.AuthorId == file.AuthorId && b.NormalizedTitle == normalizedGuess)
                        .FirstOrDefaultAsync(ct);

                    if (match is not null && await LinkBlocklist.IsBlockedAsync(db, file.FullPath, match.Id, ct))
                        match = null; // this exact pairing was explicitly unlinked before — never re-add it

                    if (match is not null)
                    {
                        file.BookId = match.Id;
                        file.ManuallyUnmatched = false;
                        matched++;

                        // Log each successful match individually to the Activity page
                        var fileName = Path.GetFileName(file.FullPath);
                        ActivityLogger.Record(db, "llm-title-match",
                            $"Matched \"{fileName}\" → \"{match.Title}\" by {file.Author.Name} (LLM found: \"{guess.Title}\")",
                            source: "llm-title-match");

                        _log.LogInformation(
                            "LLM title match: linked file #{FileId} to book #{BookId} \"{Title}\" via LLM guess \"{Guess}\"",
                            file.Id, match.Id, match.Title, guess.Title);
                    }
                }
            }

            await db.SaveChangesAsync(ct);
        }

        await SaveUsageAsync(db, today, usedToday + calls, ct);

        // Log a summary entry when the job runs with no matches or to show completion
        if (calls > 0 && matched == 0)
        {
            // No matches found - log summary
            ActivityLogger.Record(db, "llm-title-match",
                $"LLM ({cfg.Provider}) tried {considered} unmatched file(s) for starred authors in {calls} call(s) — no title matches found",
                source: "llm-title-match");
        }
        else if (calls > 0 && matched > 0)
        {
            // Matches were logged individually above; add completion summary
            ActivityLogger.Record(db, "llm-title-match",
                $"LLM ({cfg.Provider}) completed: matched {matched} of {considered} file(s) in {calls} call(s)",
                source: "llm-title-match");
        }
        else if (considered > 0)
        {
            // Considered files but didn't call LLM (all already attempted or budget exhausted)
            ActivityLogger.Record(db, "llm-title-match",
                $"LLM title-match: found {considered} candidate(s) but skipped (already attempted or budget exhausted)",
                source: "llm-title-match");
        }
        await db.SaveChangesAsync(ct);

        _log.LogInformation("LLM title match: considered {Considered}, matched {Matched}, calls {Calls}", considered, matched, calls);
        _currentMessage = $"Done — matched {matched} of {considered} ({calls} call(s))";
        return new LlmTitleMatchSummary(considered, matched, calls, true);
    }

    // Shared usage tracking with LlmIdentificationService — both consume the
    // same paid LLM API, so the daily budget is a single pool.
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

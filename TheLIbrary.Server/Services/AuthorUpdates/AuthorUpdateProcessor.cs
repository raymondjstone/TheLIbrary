using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.OpenLibrary;

namespace TheLibrary.Server.Services.AuthorUpdates;

public sealed record AuthorUpdateResult(
    int DaysProcessed,
    int MergesSeen,
    int AuthorsUpdated,
    int AuthorsRemoved,
    DateOnly LastProcessedDate,
    IReadOnlyList<string> Log);

public sealed record AuthorUpdateProgress(
    DateOnly? CurrentDay,
    int DaysProcessed,
    int DaysTotal,
    int MergesSeen,
    int AuthorsUpdated,
    int AuthorsRemoved,
    string? Message,
    string? LogLine);

// Walks the OpenLibrary recentchanges/merge-authors feed day by day, picking
// up from the last remembered date (re-processes that date, then advances to
// yesterday). For each merge it rewrites any local Author.OpenLibraryKey that
// points at a duplicate so it now points at the surviving master, and folds
// rows together when both sides exist locally.
public sealed class AuthorUpdateProcessor
{
    // First day the feed is walked from on a clean install. User-specified.
    private const string DefaultStartDate = "2026-01-01";

    private readonly LibraryDbContext _db;
    private readonly OpenLibraryClient _ol;
    private readonly ILogger<AuthorUpdateProcessor> _log;

    public AuthorUpdateProcessor(LibraryDbContext db, OpenLibraryClient ol, ILogger<AuthorUpdateProcessor> log)
    {
        _db = db; _ol = ol; _log = log;
    }

    public Task<AuthorUpdateResult> ProcessAsync(CancellationToken ct)
        => ProcessAsync(null, ct);

    public async Task<AuthorUpdateResult> ProcessAsync(Action<AuthorUpdateProgress>? onProgress, CancellationToken ct)
    {
        var log = new List<string>();
        int mergesSeen = 0, authorsUpdated = 0, authorsRemoved = 0, daysProcessed = 0;

        var setting = await _db.AppSettings
            .FirstOrDefaultAsync(s => s.Key == AppSettingKeys.AuthorUpdateLastDate, ct);
        var startDate = ParseDate(setting?.Value) ?? DateOnly.Parse(DefaultStartDate, CultureInfo.InvariantCulture);
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));

        if (startDate > yesterday)
        {
            onProgress?.Invoke(new AuthorUpdateProgress(
                null, 0, 0, 0, 0, 0,
                $"Already up to date (last processed {startDate:yyyy-MM-dd})", null));
            return new AuthorUpdateResult(0, 0, 0, 0, startDate, log);
        }

        var totalDays = (yesterday.DayNumber - startDate.DayNumber) + 1;
        DateOnly? currentDay = null;

        void Report(string? message, string? line = null)
        {
            if (line is not null) log.Add(line);
            onProgress?.Invoke(new AuthorUpdateProgress(
                currentDay, daysProcessed, totalDays,
                mergesSeen, authorsUpdated, authorsRemoved, message, line));
        }

        Report($"Processing {totalDays} day(s) from {startDate:yyyy-MM-dd} to {yesterday:yyyy-MM-dd}",
            $"Starting author-updates: {startDate:yyyy-MM-dd} -> {yesterday:yyyy-MM-dd}");

        for (var day = startDate; day <= yesterday; day = day.AddDays(1))
        {
            ct.ThrowIfCancellationRequested();
            currentDay = day;
            Report($"Fetching merges for {day:yyyy-MM-dd}");

            var merges = await _ol.FetchAuthorMergesAsync(day, ct);
            int dayUpdates = 0, dayRemovals = 0;

            foreach (var merge in merges)
            {
                ct.ThrowIfCancellationRequested();
                mergesSeen++;

                var masterKey = StripAuthorPrefix(merge.Data?.Master);
                if (masterKey is null || merge.Data?.Duplicates is null) continue;

                foreach (var dup in merge.Data.Duplicates)
                {
                    var dupKey = StripAuthorPrefix(dup);
                    if (dupKey is null || string.Equals(dupKey, masterKey, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var holder = await _db.Authors
                        .FirstOrDefaultAsync(a => a.OpenLibraryKey == dupKey, ct);
                    if (holder is null) continue;

                    var canonical = await _db.Authors
                        .FirstOrDefaultAsync(a => a.OpenLibraryKey == masterKey, ct);

                    if (canonical is null)
                    {
                        holder.OpenLibraryKey = masterKey;
                        // Clear the schedule so the next sync re-fetches works
                        // under the new key (the old key's book rows stay but
                        // will be deduped against fresh results).
                        holder.NextFetchAt = null;
                        authorsUpdated++; dayUpdates++;
                        Report(null, $"{day:yyyy-MM-dd}: rekey '{holder.Name}' {dupKey} -> {masterKey}");
                    }
                    else
                    {
                        // Both sides exist locally — fold holder into canonical.
                        // Mirrors ResolveAndFetchAuthorAsync's duplicate handling:
                        // null out LocalBookFile FKs (NoAction delete would fail),
                        // then remove the holder row. Phase 4 of the next sync
                        // rematches those files against the surviving author.
                        if (string.IsNullOrEmpty(canonical.CalibreFolderName)
                            && !string.IsNullOrEmpty(holder.CalibreFolderName))
                        {
                            canonical.CalibreFolderName = holder.CalibreFolderName;
                        }

                        await _db.LocalBookFiles
                            .Where(f => f.AuthorId == holder.Id)
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(f => f.AuthorId, _ => null)
                                .SetProperty(f => f.BookId, _ => null), ct);

                        _db.Authors.Remove(holder);
                        authorsRemoved++; dayRemovals++;
                        Report(null, $"{day:yyyy-MM-dd}: merged '{holder.Name}' -> '{canonical.Name}' ({masterKey})");
                    }

                    // Flush per merge so a later merge on the same day that
                    // references this author sees the new key.
                    await _db.SaveChangesAsync(ct);
                }
            }

            // Persist the cursor after every day so a mid-range crash resumes
            // correctly. We record the LAST date we finished — the next run
            // re-processes this date before moving on, which is deliberate:
            // the feed is append-only within a day so re-processing is a no-op
            // if nothing new landed.
            await UpsertLastDateAsync(day, ct);
            daysProcessed++;

            if (merges.Count > 0 || dayUpdates > 0 || dayRemovals > 0)
            {
                Report(null, $"{day:yyyy-MM-dd}: {merges.Count} merges seen, {dayUpdates} rekeyed, {dayRemovals} folded");
            }
        }

        Report($"Done — processed {daysProcessed} day(s), {authorsUpdated} rekeyed, {authorsRemoved} folded",
            $"Finished at {yesterday:yyyy-MM-dd}");

        return new AuthorUpdateResult(daysProcessed, mergesSeen, authorsUpdated, authorsRemoved, yesterday, log);
    }

    private async Task UpsertLastDateAsync(DateOnly date, CancellationToken ct)
    {
        var iso = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var row = await _db.AppSettings
            .FirstOrDefaultAsync(s => s.Key == AppSettingKeys.AuthorUpdateLastDate, ct);
        if (row is null)
            _db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.AuthorUpdateLastDate, Value = iso });
        else
            row.Value = iso;
        await _db.SaveChangesAsync(ct);
    }

    private static DateOnly? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;
    }

    // Feed returns "/authors/OL1234A"; local storage drops the prefix.
    private static string? StripAuthorPrefix(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        const string prefix = "/authors/";
        return raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? raw[prefix.Length..]
            : raw;
    }
}

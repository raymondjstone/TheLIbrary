using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Services.AuthorUpdates;

public sealed record AuthorUpdateResult(
    int DaysProcessed,
    int MergesSeen,
    int AuthorsUpdated,
    int AuthorsRemoved,
    int CatalogInserted,
    int CatalogUpdated,
    DateOnly LastProcessedDate,
    IReadOnlyList<string> Log);

public sealed record AuthorUpdateProgress(
    DateOnly? CurrentDay,
    int DaysProcessed,
    int DaysTotal,
    int MergesSeen,
    int AuthorsUpdated,
    int AuthorsRemoved,
    int CatalogInserted,
    int CatalogUpdated,
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
        int catalogInserted = 0, catalogUpdated = 0;

        var setting = await _db.AppSettings
            .FirstOrDefaultAsync(s => s.Key == AppSettingKeys.AuthorUpdateLastDate, ct);
        var startDate = ParseDate(setting?.Value) ?? DateOnly.Parse(DefaultStartDate, CultureInfo.InvariantCulture);
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));

        if (startDate > yesterday)
        {
            onProgress?.Invoke(new AuthorUpdateProgress(
                null, 0, 0, 0, 0, 0, 0, 0,
                $"Already up to date (last processed {startDate:yyyy-MM-dd})", null));
            return new AuthorUpdateResult(0, 0, 0, 0, 0, 0, startDate, log);
        }

        var totalDays = (yesterday.DayNumber - startDate.DayNumber) + 1;
        DateOnly? currentDay = null;

        void Report(string? message, string? line = null)
        {
            if (line is not null) log.Add(line);
            onProgress?.Invoke(new AuthorUpdateProgress(
                currentDay, daysProcessed, totalDays,
                mergesSeen, authorsUpdated, authorsRemoved,
                catalogInserted, catalogUpdated, message, line));
        }

        Report($"Processing {totalDays} day(s) from {startDate:yyyy-MM-dd} to {yesterday:yyyy-MM-dd}",
            $"Starting author-updates: {startDate:yyyy-MM-dd} -> {yesterday:yyyy-MM-dd}");

        for (var day = startDate; day <= yesterday; day = day.AddDays(1))
        {
            ct.ThrowIfCancellationRequested();
            currentDay = day;
            Report($"Fetching merges for {day:yyyy-MM-dd}");

            var merges = await _ol.FetchAuthorMergesAsync(day, ct);
            int dayUpdates = 0, dayRemovals = 0, dayCatalogInserted = 0, dayCatalogUpdated = 0;

            foreach (var merge in merges)
            {
                ct.ThrowIfCancellationRequested();
                mergesSeen++;

                var masterKey = StripAuthorPrefix(merge.Data?.Master);
                if (masterKey is null) continue;

                // Build the dup-key list from data.duplicates when present; fall
                // back to the changes array (filter to /authors/ keys, exclude
                // master) so the catalog is always fully updated even when data
                // is sparse.
                List<string> dupKeys;
                if (merge.Data?.Duplicates is { Count: > 0 })
                {
                    dupKeys = merge.Data.Duplicates
                        .Select(StripAuthorPrefix)
                        .Where(k => k is not null && !string.Equals(k, masterKey, StringComparison.OrdinalIgnoreCase))
                        .Select(k => k!)
                        .ToList();
                }
                else
                {
                    dupKeys = (merge.Changes ?? [])
                        .Select(c => StripAuthorPrefix(c.Key))
                        .Where(k => k is not null
                            && k.StartsWith("OL", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(k, masterKey, StringComparison.OrdinalIgnoreCase))
                        .Select(k => k!)
                        .ToList();
                }

                // Update local Authors table.
                foreach (var dupKey in dupKeys)
                {
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

                    // Flush per dup so a later merge on the same day that
                    // references this author sees the new key.
                    await _db.SaveChangesAsync(ct);
                }

                // Upsert master into the OpenLibraryAuthors catalog regardless
                // of whether any local Author rows were affected — the catalog
                // must stay current so Phase 2 of sync can auto-match folders
                // to the surviving master key.
                var masterInfo = await _ol.FetchAuthorAsync(masterKey, ct);
                if (masterInfo is null)
                {
                    _log.LogWarning("FetchAuthorAsync returned null for master {Key}; skipping catalog upsert", masterKey);
                }
                else if (masterInfo.Name is null)
                {
                    _log.LogWarning("Author {Key} has no name (redirect or deleted record); skipping catalog upsert", masterKey);
                }

                if (masterInfo?.Name is not null)
                {
                    var catalogRow = await _db.OpenLibraryAuthors
                        .FirstOrDefaultAsync(a => a.OlKey == masterKey, ct);
                    if (catalogRow is not null)
                    {
                        catalogRow.Name = masterInfo.Name;
                        catalogRow.NormalizedName = TitleNormalizer.NormalizeAuthor(masterInfo.Name);
                        catalogRow.PersonalName = masterInfo.PersonalName;
                        catalogRow.AlternateNames = masterInfo.AlternateNames is { Count: > 0 }
                            ? string.Join("; ", masterInfo.AlternateNames)
                            : null;
                        catalogRow.BirthDate = masterInfo.BirthDate;
                        catalogRow.DeathDate = masterInfo.DeathDate;
                        catalogRow.ImportedAt = DateTime.UtcNow;
                        catalogUpdated++; dayCatalogUpdated++;
                    }
                    else
                    {
                        _db.OpenLibraryAuthors.Add(new OpenLibraryAuthor
                        {
                            OlKey = masterKey,
                            Name = masterInfo.Name,
                            NormalizedName = TitleNormalizer.NormalizeAuthor(masterInfo.Name),
                            PersonalName = masterInfo.PersonalName,
                            AlternateNames = masterInfo.AlternateNames is { Count: > 0 }
                                ? string.Join("; ", masterInfo.AlternateNames)
                                : null,
                            BirthDate = masterInfo.BirthDate,
                            DeathDate = masterInfo.DeathDate,
                            ImportedAt = DateTime.UtcNow,
                        });
                        catalogInserted++; dayCatalogInserted++;
                        Report(null, $"{day:yyyy-MM-dd}: catalog +'{masterInfo.Name}' ({masterKey})");
                    }
                    await _db.SaveChangesAsync(ct);
                }

                // Remove the now-defunct duplicate keys from the catalog.
                if (dupKeys.Count > 0)
                    await _db.OpenLibraryAuthors
                        .Where(a => dupKeys.Contains(a.OlKey))
                        .ExecuteDeleteAsync(ct);
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
                Report(null, $"{day:yyyy-MM-dd}: {merges.Count} merges seen, {dayUpdates} rekeyed, {dayRemovals} folded, {dayCatalogInserted} catalog inserted, {dayCatalogUpdated} catalog updated");
            }
        }

        Report($"Done — processed {daysProcessed} day(s), {authorsUpdated} rekeyed, {authorsRemoved} folded, {catalogInserted} catalog inserted, {catalogUpdated} catalog updated",
            $"Finished at {yesterday:yyyy-MM-dd}");

        return new AuthorUpdateResult(daysProcessed, mergesSeen, authorsUpdated, authorsRemoved, catalogInserted, catalogUpdated, yesterday, log);
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

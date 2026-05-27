using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Services.Pushover;

namespace TheLibrary.Server.Services.Sync;

public sealed record AuthorRefreshOutcome(
    int AuthorId,
    bool MergedIntoCanonical,
    int? CanonicalAuthorId,
    string Status,
    string? ExclusionReason,
    int BooksAdded,
    int TotalBooks,
    DateTime? NextFetchAt);

// Single-author flavour of phase 3 of the full sync. Resolves the OL key if
// missing, fetches English works, applies exclusion rules, and updates the
// schedule. Used both by SyncService (per-author inside the big loop) and by
// the AuthorsController's on-demand refresh endpoint.
public sealed class AuthorRefresher
{
    // Author is excluded if every work's first_publish_year is < this.
    public const int MinPublishYear = 1930;

    // Manual-book title score at/above which a fetched OL work is treated as
    // the same book, so the manual row is promoted in place.
    private const double ManualPromotionTitleThreshold = 0.92;

    // Extracts a series position from OL title parentheticals. Handles:
    //   (Series, #3)  (Series, Book 3)  (Series, Book #3)  (Series, 3)
    //   (Series Book 3)  (Series #3)  (Book 3)  (Part 2)  (Vol. 4)
    private static readonly Regex SeriesPosRx = new(
        @"\([^)]+?(?:,\s*|\s+)(?:book\s+|part\s+|vol(?:ume)?\s*\.?\s*)?#?(\d+(?:\.\d+)?)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Extracts both the series name and position from OL title parentheticals
    // when OL's series field is absent. Two patterns:
    //   Comma:  (Series Name, [Book/Part/Vol] [#]N)  — unambiguous split on comma
    //   Space:  (Series Name Book/Part/Vol [#]N)     — requires explicit keyword
    private static readonly Regex SeriesInfoRx = new(
        @"\(([^),]+?),\s*(?:book\s+|part\s+|vol(?:ume)?\s*\.?\s*)?#?(\d+(?:\.\d+)?)\s*\)" +
        @"|\(([^)]+?)\s+(?:book|part|vol(?:ume)?\.?)\s+#?(\d+(?:\.\d+)?)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly LibraryDbContext _db;
    private readonly OpenLibraryClient _ol;
    private readonly AuthorRefreshCoordinator _coordinator;
    private readonly PushoverClient _pushover;
    private readonly ILogger<AuthorRefresher> _log;

    public sealed record RefreshCadenceSettings(int RecentDays, int MidDays, int DormantDays, int OldOrEmptyDays)
    {
        public static readonly RefreshCadenceSettings Defaults = new(2, 14, 28, 60);
    }

    public AuthorRefresher(
        LibraryDbContext db,
        OpenLibraryClient ol,
        AuthorRefreshCoordinator coordinator,
        PushoverClient pushover,
        ILogger<AuthorRefresher> log)
    {
        _db = db; _ol = ol; _coordinator = coordinator; _pushover = pushover; _log = log;
    }

    public async Task<AuthorRefreshOutcome> RefreshAsync(Author author, Action<string>? onMessage, CancellationToken ct)
    {
        if (!_coordinator.TryAcquire(author.Id))
            throw new AuthorRefreshAlreadyRunningException(author.Id, author.Name);

        try
        {
            return await RefreshCoreAsync(author, onMessage, ct);
        }
        finally
        {
            _coordinator.Release(author.Id);
        }
    }

    private async Task<AuthorRefreshOutcome> RefreshCoreAsync(Author author, Action<string>? onMessage, CancellationToken ct)
    {
        onMessage?.Invoke($"Resolving {author.Name}");

        if (string.IsNullOrEmpty(author.OpenLibraryKey))
        {
            var searchName = author.CalibreFolderName ?? author.Name;
            // Calibre writes "Last, First" — flip so OL search ranks better.
            if (searchName.Contains(','))
            {
                var parts = searchName.Split(',', 2, StringSplitOptions.TrimEntries);
                searchName = $"{parts[1]} {parts[0]}".Trim();
            }

            var search = await _ol.SearchAuthorsAsync(searchName, ct);
            var best = PickBestAuthor(search?.Docs, searchName);
            if (best?.Key is null)
            {
                author.Status = AuthorStatus.NotFound;
                author.ExclusionReason = $"No OpenLibrary match for '{searchName}'";
                author.LastSyncedAt = DateTime.UtcNow;
                // No works to base an interval on — defer the longest bucket
                // so unresolved authors don't get retried every run.
                author.NextFetchAt = author.LastSyncedAt.Value.AddDays(
                    author.RefreshIntervalDays ?? 28);
                await _db.SaveChangesAsync(ct);
                return new AuthorRefreshOutcome(
                    author.Id, false, null, author.Status.ToString(),
                    author.ExclusionReason, 0, 0, author.NextFetchAt);
            }

            // Another Author row might already own this OL key — two Calibre
            // folder spellings that both resolve to the same person, or a
            // manual-add-plus-auto-create pair. Fold this row into the canonical
            // one instead of letting the unique index blow up.
            var canonical = await _db.Authors.FirstOrDefaultAsync(
                a => a.Id != author.Id && a.OpenLibraryKey == best.Key, ct);
            if (canonical is not null)
            {
                // If the user has explicitly linked this row to another author,
                // honour that intent — do NOT auto-delete on OL collision. We
                // also stop trying to assign an OL key (the unique index would
                // reject it anyway). Defer the next refresh; canonical's own
                // refresh already covers any shared OL data, and the link makes
                // child books appear under the canonical via the merged view.
                if (author.LinkedToAuthorId is not null)
                {
                    author.LastSyncedAt = DateTime.UtcNow;
                    author.NextFetchAt = author.LastSyncedAt.Value.AddDays(
                        author.RefreshIntervalDays ?? 28);
                    await _db.SaveChangesAsync(ct);
                    _log.LogInformation(
                        "Refresh skipped for '{Name}' (id {Id}) — user-linked to author {LinkedId}; OL key {Key} owned by canonical row {CanonId}",
                        author.Name, author.Id, author.LinkedToAuthorId, best.Key, canonical.Id);
                    return new AuthorRefreshOutcome(
                        author.Id, false, null, author.Status.ToString(),
                        "Linked to another author — refresh deferred", 0, 0, author.NextFetchAt);
                }

                if (string.IsNullOrEmpty(canonical.CalibreFolderName) && !string.IsNullOrEmpty(author.CalibreFolderName))
                    canonical.CalibreFolderName = author.CalibreFolderName;

                // LocalBookFile.Author is NoAction; null the FKs so the author
                // delete doesn't violate the constraint. They get rematched in
                // Phase 4 against the canonical author.
                await _db.LocalBookFiles.Where(f => f.AuthorId == author.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(f => f.AuthorId, _ => null)
                                              .SetProperty(f => f.BookId, _ => null), ct);

                _db.Authors.Remove(author);
                await _db.SaveChangesAsync(ct);
                _log.LogInformation("Merged duplicate author '{Name}' (id {Id}) into canonical '{CanonName}' (OL {Key})",
                    author.Name, author.Id, canonical.Name, best.Key);
                return new AuthorRefreshOutcome(
                    author.Id, true, canonical.Id, "MergedIntoCanonical",
                    null, 0, 0, null);
            }

            author.OpenLibraryKey = best.Key;
            if (!string.IsNullOrWhiteSpace(best.Name)) author.Name = best.Name!;
            author.WorkCount = best.WorkCount;
            await _db.SaveChangesAsync(ct);
        }

        onMessage?.Invoke($"Fetching works for {author.Name}");

        // Snapshot before mutation: skip Pushover alerts on the very first
        // refresh of an author so adding a watchlist entry doesn't fire one
        // notification per book backfilled.
        var isInitialRefresh = author.LastSyncedAt is null;
        var notificationsQueued = new List<(string Title, int? Year, string WorkKey)>();

        // Load existing books as a dict so we can both deduplicate and
        // backfill subjects/series for books that predate this feature.
        var existingBooks = await _db.Books
            .Where(b => b.AuthorId == author.Id)
            .Select(b => new { b.Id, b.OpenLibraryWorkKey, b.Subjects, b.SeriesId, b.SeriesPosition })
            .ToListAsync(ct);
        var existingByKey = existingBooks.ToDictionary(
            b => b.OpenLibraryWorkKey, b => b, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(existingByKey.Keys, StringComparer.OrdinalIgnoreCase);

        // Books the user catalogued by hand (synthetic "XX" keys) that OL might
        // now list. Keyed by normalized title: a fetched OL work with a
        // matching title promotes the manual row in place — its Book.Id is
        // kept, so the row's series link, local files, read status and
        // ownership all carry over untouched.
        var manualBooks = await _db.Books
            .Where(b => b.AuthorId == author.Id
                     && b.OpenLibraryWorkKey.StartsWith(ManualWorkKey.Prefix))
            .ToListAsync(ct);

        // Starred authors (Priority >= 1) bypass the English-only filter so
        // works in any language are retrieved.
        var worksStream = author.Priority >= 1
            ? _ol.GetAllWorksAsync(author.OpenLibraryKey!, ct)
            : _ol.GetEnglishWorksAsync(author.OpenLibraryKey!, ct);

        int fetched = 0;
        int promoted = 0;
        await foreach (var doc in worksStream)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(doc.Key) || string.IsNullOrWhiteSpace(doc.Title)) continue;

            var workKey = doc.Key.Split('/').Last();

            var seriesName = string.IsNullOrWhiteSpace(doc.Series?.FirstOrDefault())
                ? null : doc.Series!.First().Trim();
            string? seriesPos;
            if (seriesName is not null)
            {
                seriesPos = ParseSeriesPosition(doc.Title!);
            }
            else
            {
                (seriesName, seriesPos) = ParseSeriesInfoFromTitle(doc.Title!);
            }

            if (!seen.Add(workKey))
            {
                // Book already exists — backfill subjects/series/position when not yet set.
                // Subjects null = never checked; "" = checked, OL had nothing (don't retry).
                if (existingByKey.TryGetValue(workKey, out var existing))
                {
                    bool needsSubjects = existing.Subjects is null;
                    bool needsSeriesName = existing.SeriesId is null && seriesName is not null;
                    bool needsPosition = existing.SeriesPosition is null && seriesPos is not null;
                    if (needsSubjects || needsSeriesName || needsPosition)
                    {
                        int? newSeriesId = existing.SeriesId;
                        if (needsSeriesName && seriesName is not null)
                        {
                            var s = await FindOrCreateSeriesAsync(seriesName, author.Id, ct);
                            newSeriesId = s.Id;
                        }
                        await _db.Books
                            .Where(b => b.Id == existing.Id)
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(b => b.Subjects, _ => needsSubjects ? BuildSubjects(doc.Subject) : existing.Subjects)
                                .SetProperty(b => b.SeriesId, _ => newSeriesId)
                                .SetProperty(b => b.SeriesPosition, _ => needsPosition ? seriesPos : existing.SeriesPosition), ct);
                    }
                }
                continue;
            }

            // If the user already catalogued this title by hand, promote that
            // row to the real OL work instead of inserting a duplicate. The
            // OL-sourced fields are refreshed; the series, position, read
            // status and ownership the user set are left as they were.
            var normTitle = TitleNormalizer.Normalize(doc.Title);
            var manual = PickManualToPromote(manualBooks, normTitle);
            if (manual is not null)
            {
                manualBooks.Remove(manual);
                manual.OpenLibraryWorkKey = workKey;
                manual.Title = doc.Title!;
                manual.NormalizedTitle = normTitle;
                if (doc.FirstPublishYear is not null) manual.FirstPublishYear = doc.FirstPublishYear;
                if (doc.CoverId is not null) manual.CoverId = doc.CoverId;
                if (string.IsNullOrEmpty(manual.Subjects)) manual.Subjects = BuildSubjects(doc.Subject);
                if (manual.SeriesId is null && seriesName is not null)
                    manual.SeriesId = (await FindOrCreateSeriesAsync(seriesName, author.Id, ct)).Id;
                if (manual.SeriesPosition is null && seriesPos is not null)
                    manual.SeriesPosition = seriesPos;
                promoted++;
                onMessage?.Invoke($"Linked manual entry \"{manual.Title}\" to OpenLibrary work {workKey}");
                continue;
            }

            int? seriesId = null;
            if (seriesName is not null)
            {
                var seriesRecord = await FindOrCreateSeriesAsync(seriesName, author.Id, ct);
                seriesId = seriesRecord.Id;
            }

            _db.Books.Add(new Book
            {
                OpenLibraryWorkKey = workKey,
                Title = doc.Title!,
                NormalizedTitle = normTitle,
                FirstPublishYear = doc.FirstPublishYear,
                CoverId = doc.CoverId,
                AuthorId = author.Id,
                Subjects = BuildSubjects(doc.Subject), // "" when OL has none
                SeriesId = seriesId,
                SeriesPosition = seriesPos,
            });
            fetched++;

            // Queue a Pushover alert if the author opted in, this isn't the
            // first refresh (avoids backfill spam), and the book's publish
            // year is "recent". FirstPublishYear is the most precise publish
            // signal OL exposes via the works endpoint; if it's missing we
            // err on the side of not notifying — better silent than spammy.
            if (author.NotifyOnNewBooks && !isInitialRefresh
                && doc.FirstPublishYear is int year
                && year >= DateTime.UtcNow.Year - 1)
            {
                notificationsQueued.Add((doc.Title!, year, workKey));
            }

            if (fetched % 50 == 0) await _db.SaveChangesAsync(ct);
        }
        await _db.SaveChangesAsync(ct);

        if (promoted > 0)
            _log.LogInformation(
                "Promoted {Count} manually-added book(s) to OpenLibrary works for '{Name}'",
                promoted, author.Name);

        // Exclusion rules evaluated over all stored books for idempotency.
        var years = await _db.Books
            .Where(b => b.AuthorId == author.Id && b.FirstPublishYear != null)
            .Select(b => b.FirstPublishYear!.Value).ToListAsync(ct);
        var bookCount = await _db.Books.CountAsync(b => b.AuthorId == author.Id, ct);

        // Starred authors are always Active — the user explicitly wants them
        // tracked regardless of date or language criteria.
        if (author.Priority >= 1)
        {
            author.Status = AuthorStatus.Active;
            author.ExclusionReason = null;
        }
        else if (bookCount == 0)
        {
            author.Status = AuthorStatus.Excluded;
            author.ExclusionReason = "No English works returned by OpenLibrary";
        }
        else if (years.Count > 0 && years.Max() < MinPublishYear)
        {
            author.Status = AuthorStatus.Excluded;
            author.ExclusionReason = $"All English works predate {MinPublishYear}";
        }
        else
        {
            author.Status = AuthorStatus.Active;
            author.ExclusionReason = null;
        }
        author.LastSyncedAt = DateTime.UtcNow;
        author.NextFetchAt = author.LastSyncedAt.Value.Add(
            author.RefreshIntervalDays.HasValue
                ? TimeSpan.FromDays(author.RefreshIntervalDays.Value)
                : await NextFetchIntervalAsync(years, ct));

        // Fetch and store author bio if we don't have one yet.
        if (string.IsNullOrWhiteSpace(author.Bio))
        {
            var detail = await _ol.FetchAuthorAsync(author.OpenLibraryKey!, ct);
            if (detail?.Bio is { } bio)
                author.Bio = ExtractBio(bio);
        }

        // Name-collision check: this refresh might have just produced the
        // second matching name in the system, or filled in the OL key that
        // unlocks disambiguation for an existing collision. Update every
        // group member's CalibreFolderName to its resolved value.
        var all = await _db.Authors.ToListAsync(ct);
        var group = AuthorFolderNameResolver.FindCollisionGroup(author, all);
        if (group.Count >= 2)
        {
            foreach (var member in group)
            {
                var target = AuthorFolderNameResolver.Resolve(member, all);
                if (!string.Equals(member.CalibreFolderName, target, StringComparison.Ordinal))
                {
                    var tracked = member.Id == author.Id
                        ? author
                        : await _db.Authors.FirstAsync(a => a.Id == member.Id, ct);
                    tracked.CalibreFolderName = target;
                }
            }
        }

        await _db.SaveChangesAsync(ct);

        // Fire-and-forget Pushover dispatch. Failures are logged inside the
        // client; we don't surface them through the refresh outcome.
        if (notificationsQueued.Count > 0)
        {
            foreach (var (title, year, workKey) in notificationsQueued)
            {
                var message = year.HasValue
                    ? $"{author.Name} — \"{title}\" ({year.Value})"
                    : $"{author.Name} — \"{title}\"";
                var url = $"https://openlibrary.org/works/{workKey}";
                var result = await _pushover.SendAsync(
                    title: "New book detected",
                    message: message,
                    url: url,
                    ct);
                if (!result.Sent)
                    _log.LogWarning(
                        "Pushover alert for '{Title}' by {Author} not sent: {Error}",
                        title, author.Name, result.Error);
                else
                    onMessage?.Invoke($"Pushover alert sent for \"{title}\"");
            }
        }

        return new AuthorRefreshOutcome(
            author.Id, false, null, author.Status.ToString(),
            author.ExclusionReason, fetched, bookCount, author.NextFetchAt);
    }

    // Bucket the author's most-recent publication year into a refresh cadence.
    // Active-today authors get checked daily; long-dormant authors only every
    // four weeks. No years on file behaves like "anything else" (4 weeks).
    public static TimeSpan NextFetchInterval(IReadOnlyList<int> years)
        => NextFetchInterval(years, RefreshCadenceSettings.Defaults);

    public static TimeSpan NextFetchInterval(IReadOnlyList<int> years, RefreshCadenceSettings cadence)
    {
        if (years.Count == 0) return TimeSpan.FromDays(cadence.OldOrEmptyDays);
        var mostRecent = years.Max();
        var age = DateTime.UtcNow.Year - mostRecent;
        if (age <= 1) return TimeSpan.FromDays(cadence.RecentDays);
        if (age <= 5) return TimeSpan.FromDays(cadence.MidDays);
        if (age <= 10) return TimeSpan.FromDays(cadence.DormantDays);
        return TimeSpan.FromDays(cadence.OldOrEmptyDays);
    }

    private async Task<TimeSpan> NextFetchIntervalAsync(IReadOnlyList<int> years, CancellationToken ct)
    {
        var rows = await _db.AppSettings
            .Where(s => s.Key == AppSettingKeys.RefreshCadenceRecentDays
                     || s.Key == AppSettingKeys.RefreshCadenceMidDays
                     || s.Key == AppSettingKeys.RefreshCadenceDormantDays
                     || s.Key == AppSettingKeys.RefreshCadenceOldOrEmptyDays)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        var cadence = new RefreshCadenceSettings(
            ReadCadence(rows, AppSettingKeys.RefreshCadenceRecentDays, RefreshCadenceSettings.Defaults.RecentDays),
            ReadCadence(rows, AppSettingKeys.RefreshCadenceMidDays, RefreshCadenceSettings.Defaults.MidDays),
            ReadCadence(rows, AppSettingKeys.RefreshCadenceDormantDays, RefreshCadenceSettings.Defaults.DormantDays),
            ReadCadence(rows, AppSettingKeys.RefreshCadenceOldOrEmptyDays, RefreshCadenceSettings.Defaults.OldOrEmptyDays));

        return NextFetchInterval(years, cadence);
    }

    private static int ReadCadence(IReadOnlyDictionary<string, string> rows, string key, int fallback)
        => rows.TryGetValue(key, out var v) && int.TryParse(v, out var n) && n > 0 ? n : fallback;

    private static AuthorSearchDoc? PickBestAuthor(List<AuthorSearchDoc>? docs, string searchName)
    {
        if (docs is null || docs.Count == 0) return null;
        var norm = TitleNormalizer.NormalizeAuthor(searchName);
        return docs.FirstOrDefault(d => TitleNormalizer.NormalizeAuthor(d.Name) == norm);
    }

    // Chooses which manually-added book (if any) a fetched OL work should
    // promote into. An exact normalized-title match wins; failing that, a
    // single clear fuzzy match above the threshold. An ambiguous tie promotes
    // nothing — a duplicate the user can merge is safer than a wrong rewrite.
    private static Book? PickManualToPromote(List<Book> manual, string normTitle)
    {
        if (manual.Count == 0 || normTitle.Length == 0) return null;

        var exact = manual
            .Where(m => string.Equals(m.NormalizedTitle, normTitle, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) return null;

        var fuzzy = manual
            .Where(m => !string.IsNullOrEmpty(m.NormalizedTitle))
            .Select(m => (Book: m, Score: FuzzyScore.JaroWinkler(m.NormalizedTitle!, normTitle)))
            .Where(x => x.Score >= ManualPromotionTitleThreshold)
            .OrderByDescending(x => x.Score)
            .ToList();
        if (fuzzy.Count == 1) return fuzzy[0].Book;
        if (fuzzy.Count > 1 && fuzzy[0].Score > fuzzy[1].Score) return fuzzy[0].Book;
        return null;
    }

    // OL subjects list → semicolon-delimited string, capped at 2000 chars.
    // Returns "" (not null) when OL has no subjects so callers can distinguish
    // "never checked" (null) from "checked, nothing found" ("").
    private static string BuildSubjects(List<string>? subjects)
    {
        if (subjects is null || subjects.Count == 0) return "";
        var joined = string.Join(";", subjects.Select(s => s.Trim()).Where(s => s.Length > 0));
        return string.IsNullOrWhiteSpace(joined) ? "" : (joined.Length > 2000 ? joined[..2000] : joined);
    }

    // Tries to parse a series position from an OL title like "Title (Series, #1)".
    internal static string? ParseSeriesPosition(string title)
    {
        var m = SeriesPosRx.Match(title);
        return m.Success ? m.Groups[1].Value : null;
    }

    // When OL's series field is absent, tries to extract both the series name
    // and position from the title parenthetical, e.g. "Title (Series, Book 3)".
    internal static (string? Name, string? Position) ParseSeriesInfoFromTitle(string title)
    {
        var m = SeriesInfoRx.Match(title);
        if (!m.Success) return (null, null);
        // Comma alternative → groups 1 & 2; space alternative → groups 3 & 4.
        var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Value;
        var pos  = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[4].Value;
        return (name.Trim(), string.IsNullOrEmpty(pos) ? null : pos);
    }

    // Finds an existing Series by NormalizedName or creates a new one.
    // Sets PrimaryAuthorId if not already set and authorId is provided.
    private async Task<Series> FindOrCreateSeriesAsync(string name, int? authorId, CancellationToken ct)
    {
        var normalizedName = TitleNormalizer.Normalize(name);
        var series = await _db.Series.FirstOrDefaultAsync(s => s.NormalizedName == normalizedName, ct);
        if (series is null)
        {
            series = new Series
            {
                Name = name.Trim(),
                NormalizedName = normalizedName,
                PrimaryAuthorId = authorId,
            };
            _db.Series.Add(series);
            await _db.SaveChangesAsync(ct);
        }
        else if (series.PrimaryAuthorId is null && authorId is not null)
        {
            series.PrimaryAuthorId = authorId;
            await _db.SaveChangesAsync(ct);
        }
        return series;
    }

    // OL stores bio as either a plain string or {"type":"/type/text","value":"..."}.
    private static string? ExtractBio(System.Text.Json.JsonElement bio)
    {
        if (bio.ValueKind == System.Text.Json.JsonValueKind.String)
        { var s = bio.GetString(); return string.IsNullOrWhiteSpace(s) ? null : s; }
        if (bio.ValueKind == System.Text.Json.JsonValueKind.Object
            && bio.TryGetProperty("value", out var v))
        { var s = v.GetString(); return string.IsNullOrWhiteSpace(s) ? null : s; }
        return null;
    }
}

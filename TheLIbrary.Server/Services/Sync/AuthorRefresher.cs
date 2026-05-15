using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.OpenLibrary;

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

    private readonly LibraryDbContext _db;
    private readonly OpenLibraryClient _ol;
    private readonly ILogger<AuthorRefresher> _log;

    public AuthorRefresher(LibraryDbContext db, OpenLibraryClient ol, ILogger<AuthorRefresher> log)
    {
        _db = db; _ol = ol; _log = log;
    }

    public async Task<AuthorRefreshOutcome> RefreshAsync(Author author, Action<string>? onMessage, CancellationToken ct)
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
                author.NextFetchAt = author.LastSyncedAt.Value.AddDays(28);
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

        // Load existing books as a dict so we can both deduplicate and
        // backfill subjects/series for books that predate this feature.
        var existingBooks = await _db.Books
            .Where(b => b.AuthorId == author.Id)
            .Select(b => new { b.Id, b.OpenLibraryWorkKey, b.Subjects, b.Series })
            .ToListAsync(ct);
        var existingByKey = existingBooks.ToDictionary(
            b => b.OpenLibraryWorkKey, b => b, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(existingByKey.Keys, StringComparer.OrdinalIgnoreCase);

        // Starred authors (Priority >= 1) bypass the English-only filter so
        // works in any language are retrieved.
        var worksStream = author.Priority >= 1
            ? _ol.GetAllWorksAsync(author.OpenLibraryKey!, ct)
            : _ol.GetEnglishWorksAsync(author.OpenLibraryKey!, ct);

        int fetched = 0;
        await foreach (var doc in worksStream)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(doc.Key) || string.IsNullOrWhiteSpace(doc.Title)) continue;

            var workKey = doc.Key.Split('/').Last();

            if (!seen.Add(workKey))
            {
                // Book already exists — backfill subjects/series only when we
                // have never checked before (Subjects is null). An empty string
                // means "checked, OL returned nothing" so we don't retry.
                if (existingByKey.TryGetValue(workKey, out var existing)
                    && existing.Subjects is null)
                {
                    var newSubjects = BuildSubjects(doc.Subject); // "" when OL has none
                    var newSeries = string.IsNullOrWhiteSpace(doc.Series?.FirstOrDefault())
                        ? existing.Series : doc.Series!.First().Trim();
                    await _db.Books
                        .Where(b => b.Id == existing.Id)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(b => b.Subjects, _ => newSubjects)
                            .SetProperty(b => b.Series, _ => newSeries), ct);
                }
                continue;
            }

            _db.Books.Add(new Book
            {
                OpenLibraryWorkKey = workKey,
                Title = doc.Title!,
                NormalizedTitle = TitleNormalizer.Normalize(doc.Title),
                FirstPublishYear = doc.FirstPublishYear,
                CoverId = doc.CoverId,
                AuthorId = author.Id,
                Subjects = BuildSubjects(doc.Subject), // "" when OL has none
                Series = string.IsNullOrWhiteSpace(doc.Series?.FirstOrDefault()) ? null : doc.Series!.First().Trim(),
            });
            fetched++;
            if (fetched % 50 == 0) await _db.SaveChangesAsync(ct);
        }
        await _db.SaveChangesAsync(ct);

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
        author.NextFetchAt = author.LastSyncedAt.Value.Add(NextFetchInterval(years));

        // Fetch and store author bio if we don't have one yet.
        if (string.IsNullOrWhiteSpace(author.Bio))
        {
            var detail = await _ol.FetchAuthorAsync(author.OpenLibraryKey!, ct);
            if (detail?.Bio is { } bio)
                author.Bio = ExtractBio(bio);
        }

        await _db.SaveChangesAsync(ct);

        return new AuthorRefreshOutcome(
            author.Id, false, null, author.Status.ToString(),
            author.ExclusionReason, fetched, bookCount, author.NextFetchAt);
    }

    // Bucket the author's most-recent publication year into a refresh cadence.
    // Active-today authors get checked daily; long-dormant authors only every
    // four weeks. No years on file behaves like "anything else" (4 weeks).
    public static TimeSpan NextFetchInterval(IReadOnlyList<int> years)
    {
        if (years.Count == 0) return TimeSpan.FromDays(60);
        var mostRecent = years.Max();
        var age = DateTime.UtcNow.Year - mostRecent;
        if (age <= 1) return TimeSpan.FromDays(2);
        if (age <= 5) return TimeSpan.FromDays(14);
        if (age <= 10) return TimeSpan.FromDays(28);
        return TimeSpan.FromDays(60);
    }

    private static AuthorSearchDoc? PickBestAuthor(List<AuthorSearchDoc>? docs, string searchName)
    {
        if (docs is null || docs.Count == 0) return null;
        var norm = TitleNormalizer.NormalizeAuthor(searchName);
        return docs.FirstOrDefault(d => TitleNormalizer.NormalizeAuthor(d.Name) == norm);
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

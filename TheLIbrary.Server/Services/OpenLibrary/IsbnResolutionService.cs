using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;

namespace TheLibrary.Server.Services.OpenLibrary;

// Resolves an ISBN to a title/author ONCE and caches it in the IsbnResolutions table,
// keyed by the normalized ISBN. Every file that carries the same code then reuses that
// one row — so a shelf of books sharing an ISBN costs a single lookup, not one per
// file. OpenLibrary is the primary source; when it has no record, the configured
// fallback providers (Google Books, Hardcover, LoC, ISBNdb, in registration order) are
// tried in turn. A miss is cached too (Title null) so it isn't retried forever — but
// only when NO source found anything AND NO source gave a definitive "not found"
// either (i.e. every source that was actually reachable was rate/quota-capped). One
// capped source no longer blocks caching a miss that another source already checked
// and confirmed — otherwise the whole backlog stalls for the rest of the day the
// moment any single source's daily quota runs out.
//
// An ISBN that comes back with NOTHING AT ALL (every source unavailable, nobody gave
// even a definitive miss) is never cached — by design, so it's retried once sources
// recover. But without a limit that's forever: a persistently-broken source, or an
// ISBN no source will ever have, gets re-picked and re-attempted on every future run.
// IsbnResolutionAttempt tracks a per-ISBN FailCount for exactly this case; once it
// reaches Settings → *ISBN metadata fallbacks* → "give up after N failed attempts",
// the ISBN is cached as a permanent miss instead — same outcome as a definitive miss,
// just reached by exhaustion instead of confirmation.
public sealed class IsbnResolutionService
{
    public const int DefaultMaxFailedAttempts = 5;

    private readonly LibraryDbContext _db;
    private readonly OpenLibraryClient _ol;
    private readonly IReadOnlyList<IIsbnFallbackProvider> _providers;

    public IsbnResolutionService(
        LibraryDbContext db, OpenLibraryClient ol, IEnumerable<IIsbnFallbackProvider> providers)
    {
        _db = db;
        _ol = ol;
        _providers = providers.ToList();
    }

    // Returns the cached resolution for an ISBN, resolving it on a miss and persisting
    // the result. Returns null only when the ISBN itself is invalid (bad length/check
    // digit). A returned row with a null WorkKey came from a fallback source (no OL
    // work); a row with everything null is a remembered "nobody had it" miss.
    //
    // Throws IsbnLookupUnavailableException when no source resolved it AND at least one
    // was temporarily unavailable (rate/quota) — nothing is cached, so it's retried
    // later (the next day, once a daily quota resets).
    public async Task<IsbnResolution?> ResolveAsync(string? rawIsbn, CancellationToken ct)
    {
        var key = IsbnResolution.IsbnKey(rawIsbn);
        if (key is null) return null;

        var existing = await _db.IsbnResolutions.FirstOrDefaultAsync(r => r.Isbn == key, ct);
        if (existing is not null)
        {
            // The row is cached — but if it has a title and NO author (an authorless OL
            // work, or a row written before author-enrichment existed), try once more to
            // fill the author from a fallback and UPDATE the row in place. Bounded to at
            // most once every 12h per row (stamped via ResolvedAt) so re-viewing the
            // Identified page can't burn fallback quota re-attempting the same ISBNs.
            if (existing.Title is not null && existing.AuthorName is null
                && _providers.Count > 0
                && existing.ResolvedAt < DateTime.UtcNow.AddHours(-12))
            {
                await TryFillAuthorAsync(existing, key, ct);
            }
            return existing;
        }

        var resp = await _ol.SearchByIsbnAsync(key, ct);
        var doc = resp?.Docs?.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.Key));
        var row = new IsbnResolution
        {
            Isbn = key,
            ResolvedAt = DateTime.UtcNow,
            WorkKey = doc?.Key,
            Title = doc?.Title,
            AuthorName = doc?.AuthorNames?.FirstOrDefault(),
            AuthorKey = doc?.AuthorKeys?.FirstOrDefault(),
            FirstPublishYear = doc?.FirstPublishYear,
            CoverId = doc?.CoverId,
        };

        // Run the fallback chain when OpenLibrary had nothing (common for
        // self-published / KDP / indie), OR when it found the work but with NO author
        // — OL records are often authorless, and "we don't know the author" is far
        // less useful than naming it. The chain fills whatever's still missing (title
        // and/or author) from the first source that has it, keeping OL's work key and
        // title. If nothing usable results and a source was temporarily unavailable,
        // don't cache — retry later.
        if ((doc is null || string.IsNullOrWhiteSpace(row.AuthorName)) && _providers.Count > 0)
        {
            var creds = await LoadCredentialsAsync(ct);
            var anyUnavailable = false;
            var anyDefinitiveMiss = false;
            foreach (var provider in _providers)
            {
                var cred = creds.GetValueOrDefault(provider.CredentialSettingKey);
                var r = await provider.LookupAsync(key, cred, ct);
                if (r.Status == IsbnLookupStatus.Hit)
                {
                    row.Title ??= r.Title;                       // keep OL's title if it had one
                    row.AuthorName ??= r.Author;
                    row.FirstPublishYear ??= r.FirstPublishYear;
                    if (!string.IsNullOrWhiteSpace(row.AuthorName)) break; // got the author we needed
                }
                else if (r.Status == IsbnLookupStatus.Unavailable) anyUnavailable = true;
                else if (r.Status == IsbnLookupStatus.Miss) anyDefinitiveMiss = true;
                // Skipped (not configured) → contributes nothing either way.
            }
            // Only defer (retry later) when NOTHING useful came back at all — no title,
            // and not even a definitive "not found" from some other source. Once one
            // source has actually checked and drawn a blank, a DIFFERENT source being
            // rate/quota-capped isn't reason enough to block caching forever: that's what
            // starved this job once Google's daily quota ran out mid-day — every
            // self-published ISBN still on the backlog got deferred and re-attempted for
            // nothing, even when Hardcover/LoC had already definitively found nobody has
            // it. If OpenLibrary already gave a title, cache it regardless — the title is
            // still useful, and the author can be re-attempted later.
            if (row.Title is null && anyUnavailable && !anyDefinitiveMiss)
            {
                if (!await RecordFailureAndCheckGiveUpAsync(key, ct))
                    throw new IsbnLookupUnavailableException();
                // Exceeded the fail-attempt limit — stop retrying and cache this as a
                // permanent miss (row is still all-null) instead of falling into the
                // same dead end on every future run.
            }
        }

        // Whatever path got us here (a real hit, a definitive miss, or giving up after
        // too many failed attempts), this ISBN now has a settled answer — drop any
        // failure-tracking row so a later re-resolution (e.g. after "reset cached
        // misses") starts its fail count fresh instead of giving up after one attempt.
        if (_providers.Count > 0)
        {
            var stale = await _db.IsbnResolutionAttempts.FirstOrDefaultAsync(a => a.Isbn == key, ct);
            if (stale is not null) _db.IsbnResolutionAttempts.Remove(stale);
        }

        _db.IsbnResolutions.Add(row);
        try
        {
            await _db.SaveChangesAsync(ct);
            return row;
        }
        catch (DbUpdateException)
        {
            // Another request resolved the same ISBN first (primary-key clash) —
            // drop ours and return the row that won the race.
            _db.Entry(row).State = EntityState.Detached;
            var winner = await _db.IsbnResolutions.AsNoTracking().FirstOrDefaultAsync(r => r.Isbn == key, ct);
            if (winner is not null) return winner;
            throw;
        }
    }

    // Bumps this ISBN's failed-attempt counter (creating the row on its first failure)
    // and reports whether it has now hit the configured limit. Saved immediately —
    // independent of the caller's own SaveChangesAsync — because the "not yet given up"
    // branch throws right after this and never reaches that later save.
    private async Task<bool> RecordFailureAndCheckGiveUpAsync(string key, CancellationToken ct)
    {
        var attempt = await _db.IsbnResolutionAttempts.FirstOrDefaultAsync(a => a.Isbn == key, ct);
        if (attempt is null)
        {
            attempt = new IsbnResolutionAttempt { Isbn = key };
            _db.IsbnResolutionAttempts.Add(attempt);
        }
        attempt.FailCount++;
        attempt.LastAttemptAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var maxRaw = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.IsbnResolveMaxFailedAttempts)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);
        var max = int.TryParse(maxRaw, out var n) && n > 0 ? n : DefaultMaxFailedAttempts;
        return attempt.FailCount >= max;
    }

    // Second-chance author fill for an already-cached row that has a title but no
    // author. Runs the fallback chain for an author only, updates the row in place, and
    // stamps ResolvedAt so the 12h re-attempt window resets (whether or not an author
    // was found — a fruitless attempt shouldn't repeat on the next page view).
    private async Task TryFillAuthorAsync(IsbnResolution existing, string key, CancellationToken ct)
    {
        var creds = await LoadCredentialsAsync(ct);
        foreach (var provider in _providers)
        {
            var cred = creds.GetValueOrDefault(provider.CredentialSettingKey);
            var r = await provider.LookupAsync(key, cred, ct);
            if (r.Status == IsbnLookupStatus.Hit && !string.IsNullOrWhiteSpace(r.Author))
            {
                existing.AuthorName = r.Author;
                existing.FirstPublishYear ??= r.FirstPublishYear;
                break;
            }
            // Miss / Skipped / Unavailable → try the next source (an unavailable one is
            // simply skipped; the 12h stamp below means we retry it later anyway).
        }
        existing.ResolvedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private async Task<Dictionary<string, string?>> LoadCredentialsAsync(CancellationToken ct)
    {
        var keys = _providers.Select(p => p.CredentialSettingKey).Distinct().ToList();
        return await _db.AppSettings.AsNoTracking()
            .Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => (string?)s.Value, ct);
    }
}

using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;

namespace TheLibrary.Server.Services.OpenLibrary;

// Resolves an ISBN to its OpenLibrary work ONCE and caches it in the IsbnResolutions
// table, keyed by the normalized ISBN. Every file that carries the same code then
// reuses that one row — so a shelf of books sharing an ISBN costs a single OL call,
// not one per file. A miss is cached too (WorkKey null) so it isn't retried forever.
public sealed class IsbnResolutionService
{
    private readonly LibraryDbContext _db;
    private readonly OpenLibraryClient _ol;
    private readonly GoogleBooksClient _google;

    public IsbnResolutionService(LibraryDbContext db, OpenLibraryClient ol, GoogleBooksClient google)
    {
        _db = db;
        _ol = ol;
        _google = google;
    }

    // Returns the cached resolution for an ISBN, resolving against OpenLibrary on a
    // miss and persisting it. Returns null only when the ISBN itself is unusable
    // (not a valid 10/13-digit length). A returned row with a null WorkKey means
    // "resolved, but OpenLibrary had nothing" — a remembered miss, not an error.
    public async Task<IsbnResolution?> ResolveAsync(string? rawIsbn, CancellationToken ct)
    {
        var key = IsbnResolution.IsbnKey(rawIsbn);
        if (key is null) return null;

        var existing = await _db.IsbnResolutions.FirstOrDefaultAsync(r => r.Isbn == key, ct);
        if (existing is not null) return existing;

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

        // OpenLibrary had nothing (the common case for self-published / KDP / indie
        // titles). Fall back to Google Books when a key is configured — it gives a
        // title/author to show (no OL work key, so Apply-by-ISBN stays inert, which
        // is correct since OL has no work to link). A Google transient error throws
        // out of here, so nothing is cached and it's retried later.
        if (doc is null)
        {
            var gbKey = await _db.AppSettings.AsNoTracking()
                .Where(s => s.Key == AppSettingKeys.GoogleBooksApiKey)
                .Select(s => s.Value)
                .FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(gbKey))
            {
                var gb = await _google.ResolveByIsbnAsync(key, gbKey, ct);
                if (gb is not null && !string.IsNullOrWhiteSpace(gb.Title))
                {
                    row.Title = gb.Title;
                    row.AuthorName = gb.Author;
                    row.FirstPublishYear = gb.FirstPublishYear;
                }
            }
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
}

using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;

namespace TheLibrary.Server.Services.Sync;

// Shared creation path for books the user catalogues by hand. Used by the
// author page's "add book" action and by resolving an unmatched physical
// inventory row. A manual book carries a synthetic "XX" work key so a later
// works-refresh can promote it in place once OpenLibrary lists the title.
public sealed class ManualBookService
{
    private readonly LibraryDbContext _db;

    public ManualBookService(LibraryDbContext db) { _db = db; }

    // Conflict == true marks the "already-catalogued" case so the caller can
    // map it to HTTP 409 rather than a generic 400.
    public sealed record Result(Book? Book, string? Error, bool Conflict);

    public async Task<Result> CreateAsync(
        int authorId,
        string? title,
        int? firstPublishYear,
        string? seriesName,
        string? seriesPosition,
        bool owned,
        CancellationToken ct)
    {
        var author = await _db.Authors.FirstOrDefaultAsync(a => a.Id == authorId, ct);
        if (author is null)
            return new Result(null, "Author not found", false);

        // Only top-level authors and pen names may own newly-created books — a
        // non-pen-name child's catalogue folds into its canonical, so a book
        // created here would never surface. Callers filter the author list
        // too; this is the backstop.
        if (author.LinkedToAuthorId is not null && !author.IsPenName)
            return new Result(null,
                "This author is linked to another — add the book under the canonical author instead.",
                false);

        var cleanTitle = title?.Trim();
        if (string.IsNullOrWhiteSpace(cleanTitle))
            return new Result(null, "Title is required", false);

        if (firstPublishYear is int y && (y < 1 || y > DateTime.UtcNow.Year + 5))
            return new Result(null, $"First publish year looks implausible: {y}", false);

        var normalizedTitle = TitleNormalizer.Normalize(cleanTitle);

        // Guard against re-cataloguing a work the author already has — manual
        // duplicates would otherwise quietly pile up alongside the OL rows.
        var clash = await _db.Books.AnyAsync(
            b => b.AuthorId == authorId && b.NormalizedTitle == normalizedTitle, ct);
        if (clash)
            return new Result(null,
                $"'{author.Name}' already has a book matching \"{cleanTitle}\".", true);

        // Resolve (or create) the series when one was supplied.
        int? seriesId = null;
        if (!string.IsNullOrWhiteSpace(seriesName))
        {
            var name = seriesName.Trim();
            var norm = TitleNormalizer.Normalize(name);
            var series = await _db.Series.FirstOrDefaultAsync(s => s.NormalizedName == norm, ct);
            if (series is null)
            {
                series = new Series { Name = name, NormalizedName = norm, PrimaryAuthorId = authorId };
                _db.Series.Add(series);
                await _db.SaveChangesAsync(ct);
            }
            else if (series.PrimaryAuthorId is null)
            {
                series.PrimaryAuthorId = authorId;
            }
            seriesId = series.Id;
        }

        // Allocate a globally-unique synthetic work key.
        string workKey;
        do { workKey = ManualWorkKey.NewCandidate(); }
        while (await _db.Books.AnyAsync(b => b.OpenLibraryWorkKey == workKey, ct));

        var book = new Book
        {
            OpenLibraryWorkKey = workKey,
            Title = cleanTitle,
            NormalizedTitle = normalizedTitle,
            FirstPublishYear = firstPublishYear,
            AuthorId = authorId,
            SeriesId = seriesId,
            SeriesPosition = string.IsNullOrWhiteSpace(seriesPosition) ? null : seriesPosition.Trim(),
            ManuallyOwned = owned,
            ManuallyOwnedAt = owned ? DateTime.UtcNow : null,
            // "" (not null) → the refresher treats subjects as "checked, none"
            // for a row with no OL work to fetch them from.
            Subjects = "",
            // Past publish year → dated to 1 Jan of that year (not "now") so the
            // book groups under its real release period in Recent Releases.
            CreatedAt = Book.CreatedAtForPublishYear(firstPublishYear),
        };
        _db.Books.Add(book);
        await _db.SaveChangesAsync(ct);

        // Record the hand-add to the activity log (book.Id is now assigned).
        var seriesText = seriesId is not null ? $" · {seriesName!.Trim()}{(string.IsNullOrWhiteSpace(seriesPosition) ? "" : $" #{seriesPosition.Trim()}")}" : "";
        var yearText = firstPublishYear is int yy ? $" ({yy})" : "";
        Services.ActivityLogger.Record(_db, "manual-add",
            $"Added manual book \"{cleanTitle}\" by {author.Name}{seriesText}{yearText}{(owned ? " · owned" : "")}",
            "user", book.Id);
        await _db.SaveChangesAsync(ct);

        return new Result(book, null, false);
    }
}

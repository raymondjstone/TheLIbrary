using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.Incoming;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Services.Import;

// In-memory index of every book in the library. Matching is tried in
// confidence order: an ISBN hit is definitive; an exact normalized/loose
// title hit needs the author to corroborate; a fuzzy fallback (TryFuzzyMatch)
// catches near-identical titles for the explicit unmatched-table rematch.
public sealed class PhysicalMatchIndex
{
    public readonly record struct BookEntry(int Id, bool ManuallyOwned, string NormAuthor);

    // Source rows the index is built from. Public so unit tests can build an
    // index in-memory without a DbContext. NormalizedTitle is still accepted
    // for backwards compatibility but is ignored — Build recomputes it from
    // Title so a stale or null stored value can't drop a book from the lookups.
    public readonly record struct SourceRow(
        int Id, string Title, string? NormalizedTitle, string? Isbn, bool ManuallyOwned, string AuthorName);

    // Per-book data retained for the fuzzy fallback.
    private readonly record struct FuzzyEntry(int Id, bool ManuallyOwned, string NormTitle);

    private readonly Dictionary<string, List<BookEntry>> _byTitle;
    private readonly Dictionary<string, List<BookEntry>> _byLoose;

    // Books bucketed by every order-variant of their author's normalized name.
    // The fuzzy fallback only ever has to score the handful of books under the
    // queried author rather than the whole library.
    private readonly Dictionary<string, List<FuzzyEntry>> _byAuthorVariant;

    // Normalized ISBN → book. An ISBN identifies an edition uniquely, so a hit
    // here is accepted with no author/title check at all.
    private readonly Dictionary<string, BookEntry> _byIsbn;

    private PhysicalMatchIndex(
        Dictionary<string, List<BookEntry>> byTitle,
        Dictionary<string, List<BookEntry>> byLoose,
        Dictionary<string, List<FuzzyEntry>> byAuthorVariant,
        Dictionary<string, BookEntry> byIsbn)
    {
        _byTitle = byTitle;
        _byLoose = byLoose;
        _byAuthorVariant = byAuthorVariant;
        _byIsbn = byIsbn;
    }

    public static async Task<PhysicalMatchIndex> LoadAsync(LibraryDbContext db, CancellationToken ct)
    {
        var raw = await db.Books.AsNoTracking()
            .Select(b => new SourceRow(b.Id, b.Title, b.NormalizedTitle, b.Isbn, b.ManuallyOwned, b.Author.Name))
            .ToListAsync(ct);
        return Build(raw);
    }

    // Construct directly from a list of source rows. Used by tests and by any
    // caller that already has the data projected (e.g. an integration test).
    public static PhysicalMatchIndex Build(IEnumerable<SourceRow> raw)
    {
        var byTitle = new Dictionary<string, List<BookEntry>>();
        var byLoose = new Dictionary<string, List<BookEntry>>();
        var byAuthorVariant = new Dictionary<string, List<FuzzyEntry>>(StringComparer.Ordinal);
        var byIsbn = new Dictionary<string, BookEntry>(StringComparer.Ordinal);

        foreach (var b in raw)
        {
            // Recompute from the raw Title rather than trusting the stored
            // Book.NormalizedTitle — that column is null on pre-feature rows
            // and goes stale if the normalizer changes, either of which would
            // silently drop the book from every lookup below.
            var normTitle = TitleNormalizer.Normalize(b.Title);
            var normAuthor = TitleNormalizer.NormalizeAuthor(b.AuthorName);
            var entry = new BookEntry(b.Id, b.ManuallyOwned, normAuthor);

            if (normTitle.Length > 0) Add(byTitle, normTitle, entry);
            var loose = PhysicalLooseKey(b.Title);
            if (loose.Length > 0) Add(byLoose, loose, entry);

            if (normTitle.Length > 0)
            {
                var fuzzy = new FuzzyEntry(b.Id, b.ManuallyOwned, normTitle);
                foreach (var variant in AuthorMatcher.ExpandNameVariants(normAuthor))
                    AddFuzzy(byAuthorVariant, variant, fuzzy);
            }

            var isbn = EpubMetadataReader.NormaliseIsbn(b.Isbn ?? "");
            if (isbn is not null) byIsbn.TryAdd(isbn, entry);
        }
        return new PhysicalMatchIndex(byTitle, byLoose, byAuthorVariant, byIsbn);

        static void Add(Dictionary<string, List<BookEntry>> map, string key, BookEntry entry)
        {
            if (!map.TryGetValue(key, out var list))
                map[key] = list = new List<BookEntry>();
            list.Add(entry);
        }

        static void AddFuzzy(Dictionary<string, List<FuzzyEntry>> map, string key, FuzzyEntry entry)
        {
            if (!map.TryGetValue(key, out var list))
                map[key] = list = new List<FuzzyEntry>();
            list.Add(entry);
        }
    }

    // Best confident match for an inventory row, or null. An ISBN hit wins
    // outright. Otherwise the title must hit the normalized or loose-key
    // lookup AND the author must match — a title hit alone is not enough,
    // since two different authors can share a title and auto-marking the
    // wrong one owned is worse than leaving the row for manual resolution.
    public BookEntry? TryMatch(string? rawTitle, string? rawAuthor, string? rawIsbn = null)
    {
        var isbn = EpubMetadataReader.NormaliseIsbn(rawIsbn ?? "");
        if (isbn is not null && _byIsbn.TryGetValue(isbn, out var isbnHit))
            return isbnHit;

        var normTitle = TitleNormalizer.Normalize(rawTitle);
        if (normTitle.Length == 0) return null;

        if (!_byTitle.TryGetValue(normTitle, out var candidates))
        {
            var loose = PhysicalLooseKey(rawTitle);
            if (loose.Length == 0 || !_byLoose.TryGetValue(loose, out candidates))
                return null;
        }

        // Author tolerance — physical-books inventories arrive in three
        // common shapes:
        //   "Piers Anthony"     → matches DB Author.Name out of the box
        //   "Anthony, Piers"    → NormalizeAuthor flips on the comma
        //   "Anthony Piers"     → no comma; needs ExpandNameVariants
        // Comparing the variant sets of both sides catches all three plus
        // their inverses (DB has "Last, First", inventory has "First Last", etc).
        var inputVariants = AuthorMatcher
            .ExpandNameVariants(TitleNormalizer.NormalizeAuthor(rawAuthor))
            .ToHashSet(StringComparer.Ordinal);

        var match = candidates.FirstOrDefault(c =>
            AuthorMatcher.ExpandNameVariants(c.NormAuthor)
                .Any(v => inputVariants.Contains(v)));
        return match is { Id: > 0 } ? match : null;
    }

    // Best fuzzy match for a (title, author) pair: the highest Jaro-Winkler
    // title score among books whose author overlaps the input author, when
    // that score clears `minTitleScore`. The author still has to match — by
    // the same order-tolerant variant rule TryMatch uses — so a near-identical
    // title on its own can't pull in the wrong book. Used by the explicit
    // unmatched-table rematch, not the conservative initial import.
    public BookEntry? TryFuzzyMatch(string? rawTitle, string? rawAuthor, double minTitleScore)
    {
        var normTitle = TitleNormalizer.Normalize(rawTitle);
        if (normTitle.Length == 0) return null;

        var seen = new HashSet<int>();
        FuzzyEntry? best = null;
        double bestScore = 0;

        // Only the books filed under one of the input author's name variants
        // are scored — a book under a different author is never even visited.
        foreach (var variant in AuthorMatcher.ExpandNameVariants(TitleNormalizer.NormalizeAuthor(rawAuthor)))
        {
            if (!_byAuthorVariant.TryGetValue(variant, out var entries)) continue;
            foreach (var e in entries)
            {
                if (!seen.Add(e.Id)) continue;
                var score = FuzzyScore.JaroWinkler(e.NormTitle, normTitle);
                if (score > bestScore) { bestScore = score; best = e; }
            }
        }
        return bestScore >= minTitleScore && best is { } b
            ? new BookEntry(b.Id, b.ManuallyOwned, "")
            : null;
    }

    // Loose key for physical-books matching: pre-replace & → and, then standard
    // normalize, then collapse all spaces. Handles hyphens vs spaces, apostrophe
    // possessives, ampersand vs "and", and general punctuation differences.
    private static readonly System.Text.RegularExpressions.Regex AmpersandRx =
        new(@"\s*&\s*", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static string PhysicalLooseKey(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var s = AmpersandRx.Replace(title, " and ");
        return TitleNormalizer.Normalize(s).Replace(" ", "");
    }
}

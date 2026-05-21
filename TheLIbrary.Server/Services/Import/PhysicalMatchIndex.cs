using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Services.Incoming;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Services.Import;

// In-memory index of every book in the library, keyed by both normalized
// title and a looser "no-spaces, & = and" key, with one TryMatch entry
// point shared by the physical-books initial import and the unmatched-table
// rematch.
public sealed class PhysicalMatchIndex
{
    public readonly record struct BookEntry(int Id, bool ManuallyOwned, string NormAuthor);

    // Source rows the index is built from. Public so unit tests can build
    // an index in-memory without a DbContext.
    public readonly record struct SourceRow(int Id, string Title, string? NormalizedTitle, bool ManuallyOwned, string AuthorName);

    private readonly Dictionary<string, List<BookEntry>> _byTitle;
    private readonly Dictionary<string, List<BookEntry>> _byLoose;

    private PhysicalMatchIndex(
        Dictionary<string, List<BookEntry>> byTitle,
        Dictionary<string, List<BookEntry>> byLoose)
    {
        _byTitle = byTitle;
        _byLoose = byLoose;
    }

    public static async Task<PhysicalMatchIndex> LoadAsync(LibraryDbContext db, CancellationToken ct)
    {
        var raw = await db.Books.AsNoTracking()
            .Select(b => new SourceRow(b.Id, b.Title, b.NormalizedTitle, b.ManuallyOwned, b.Author.Name))
            .ToListAsync(ct);
        return Build(raw);
    }

    // Construct directly from a list of source rows. Used by tests and by any
    // caller that already has the data projected (e.g. an integration test).
    public static PhysicalMatchIndex Build(IEnumerable<SourceRow> raw)
    {
        var byTitle = new Dictionary<string, List<BookEntry>>();
        var byLoose = new Dictionary<string, List<BookEntry>>();

        foreach (var b in raw)
        {
            if (string.IsNullOrEmpty(b.NormalizedTitle)) continue;
            var entry = new BookEntry(b.Id, b.ManuallyOwned,
                TitleNormalizer.NormalizeAuthor(b.AuthorName));

            Add(byTitle, b.NormalizedTitle!, entry);
            var loose = PhysicalLooseKey(b.Title);
            if (loose.Length > 0) Add(byLoose, loose, entry);
        }
        return new PhysicalMatchIndex(byTitle, byLoose);

        static void Add(Dictionary<string, List<BookEntry>> map, string key, BookEntry entry)
        {
            if (!map.TryGetValue(key, out var list))
                map[key] = list = new List<BookEntry>();
            list.Add(entry);
        }
    }

    // Returns the best matching book for a (title, author) pair, or null when
    // neither the standard nor the loose-key lookup finds anything by title.
    // When the title matches but the author doesn't, the first candidate is
    // returned anyway so the row leaves the unmatched table — caller can
    // re-check author later via the per-row UI.
    public BookEntry? TryMatch(string? rawTitle, string? rawAuthor)
    {
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

        return candidates.FirstOrDefault(c =>
                AuthorMatcher.ExpandNameVariants(c.NormAuthor)
                    .Any(v => inputVariants.Contains(v)))
            is { Id: > 0 } match
                ? match
                : candidates[0];
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

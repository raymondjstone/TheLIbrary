using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Services.Incoming;

// One indexed author — either a tracked watchlist entry or an OpenLibrary
// catalog row. FolderName is where a matched file goes:
//   - tracked: the Calibre folder already in use (e.g. author.CalibreFolderName)
//   - ol    : the OL author's display name, sanitized for quarantine grouping
//
// TrackedAuthorId is the Author.Id for tracked entries (null for OL-only).
// OpenLibraryKey is the OL author key ("OL123A") when known — populated for
// OL-derived entries and for tracked entries that already have one in DB.
// Collection folders must only be created when we hold an OpenLibraryKey, so
// downstream code can upsert the backing Author row immediately rather than
// leaving a ghost folder for the sync phase to reconcile later.
public sealed record AuthorIndexEntry(
    string DisplayName,
    string FolderName,
    bool IsTracked,
    int? TrackedAuthorId = null,
    string? OpenLibraryKey = null,
    // Additional names that should resolve to this entry — typically the OL
    // author's PersonalName + AlternateNames list. Indexed alongside
    // DisplayName and FolderName so e.g. "T. Brooks" matches "Terry Brooks"
    // when the alias is known.
    IReadOnlyList<string>? AlternateNames = null);

public sealed record AuthorMatchResult(AuthorIndexEntry Entry, string? RewrittenTitle);

// Pure in-memory author name matcher. Handles metadata-author,
// "Author - Title.ext" filenames, reverse "Title - Author.ext" filenames, and
// folder-layout ancestor matching. The caller decides where the matched file
// ultimately goes based on IsTracked.
public sealed class AuthorMatcher
{
    private readonly Dictionary<string, AuthorIndexEntry> _index = new(StringComparer.Ordinal);

    public AuthorMatcher(IEnumerable<AuthorIndexEntry> entries)
        : this(entries, null) { }

    // `blacklistedNormalized` is a set of NormalizeAuthor()'d names that
    // must never resolve — any entry whose display name or folder name
    // normalizes to one of these keys is silently skipped at index time,
    // so blacklisted authors behave as if they didn't exist in any catalog.
    public AuthorMatcher(IEnumerable<AuthorIndexEntry> entries, IEnumerable<string>? blacklistedNormalized)
    {
        var blacklist = blacklistedNormalized is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(blacklistedNormalized.Where(s => !string.IsNullOrEmpty(s)), StringComparer.Ordinal);

        // Tracked entries win any collision — they have routing intent
        // (a real folder on disk); OL entries are just reference data.
        var ordered = entries.OrderByDescending(e => e.IsTracked);
        foreach (var e in ordered)
        {
            if (blacklist.Count > 0 && IsBlacklisted(e, blacklist)) continue;
            foreach (var v in AuthorKeyVariants(e.DisplayName)) _index.TryAdd(v, e);
            foreach (var v in AuthorKeyVariants(e.FolderName)) _index.TryAdd(v, e);
            if (e.AlternateNames is not null)
                foreach (var alias in e.AlternateNames)
                    foreach (var v in AuthorKeyVariants(alias))
                        _index.TryAdd(v, e);
        }
    }

    private static bool IsBlacklisted(AuthorIndexEntry entry, HashSet<string> blacklist)
    {
        var nd = TitleNormalizer.NormalizeAuthor(entry.DisplayName);
        if (!string.IsNullOrEmpty(nd) && blacklist.Contains(nd)) return true;
        var nf = TitleNormalizer.NormalizeAuthor(entry.FolderName);
        if (!string.IsNullOrEmpty(nf) && blacklist.Contains(nf)) return true;
        return false;
    }

    public int IndexedKeyCount => _index.Count;

    // Probes the index with variants of a single raw name string. First hit wins.
    public AuthorIndexEntry? TryGet(string? rawName)
    {
        foreach (var probe in AuthorKeyVariants(rawName))
            if (_index.TryGetValue(probe, out var hit)) return hit;
        return null;
    }

    // Full single-file resolution order (same as the original IncomingProcessor):
    //   1. metadata author (or its "Last, First" sort form)
    //   2. "Author - Title.ext" filename pattern (embedded in ExtractKeys)
    //   3. reverse "Title - Author.ext" filename pattern
    //   4. each individual author of a multi-author metadata credit ("A & B",
    //      "A; B" — one EPUB dc:creator / MOBI EXTH field often carries both)
    //   5. every FilenameGuesser interpretation ("Title by Author", "[Series NN]"
    //      tags, "et al", format tags, "Last, First" inversion, …)
    // Returns null on total miss — caller then tries folder layout and finally
    // an OpenLibrary catalog lookup via GetProbeKeys.
    public AuthorMatchResult? Resolve(string? metadataAuthor, string? metadataAuthorSort, string filePath)
    {
        foreach (var (key, rewrittenTitle) in GetProbeKeys(metadataAuthor, metadataAuthorSort, filePath))
            if (_index.TryGetValue(key, out var hit))
                return new AuthorMatchResult(hit, rewrittenTitle);
        return null;
    }

    // The full list of normalized probe keys that Resolve tries, in priority
    // order (metadata first, filename-derived last). Exposed so an external
    // store (e.g. the OpenLibrary catalog) can be queried with the same keys
    // when in-memory resolution misses. Pairs each key with the rewritten
    // title to use if that key matches via a filename interpretation.
    public IEnumerable<(string Key, string? RewrittenTitle)> GetProbeKeys(
        string? metadataAuthor, string? metadataAuthorSort, string filePath)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var (primary, sort) = ExtractKeys(metadataAuthor, metadataAuthorSort, filePath);
        foreach (var k in CandidateKeys(primary, sort))
            if (seen.Add(k)) yield return (k, null);

        var reversed = TryReverseFilename(filePath);
        if (reversed is not null)
        {
            foreach (var k in AuthorKeyVariants(reversed.Value.Author))
                if (seen.Add(k)) yield return (k, reversed.Value.Title);
        }

        // A joint credit in ONE metadata field ("A & B", "A; B", "A and B")
        // normalizes to a key that matches nobody — probe each author alone.
        foreach (var raw in new[] { metadataAuthor, metadataAuthorSort })
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var parts = MultiAuthorSeparator.Split(raw);
            if (parts.Length < 2) continue;
            foreach (var part in parts)
                foreach (var k in AuthorKeyVariants(part))
                    if (seen.Add(k)) yield return (k, null);
        }

        // Filename interpretations beyond the plain dash split: "Title by
        // Author", "[Series NN] - Title - Author", "et al" credits, "(mobi)"
        // tags, "Last, First" inversion — the forms the quarantine folder is
        // actually full of.
        foreach (var g in FilenameGuesser.Interpret(filePath))
        {
            if (string.IsNullOrWhiteSpace(g.Author)) continue;
            foreach (var k in AuthorKeyVariants(g.Author))
                if (seen.Add(k)) yield return (k, g.Title);
        }
    }

    // ";", "&", "/" or a spaced "and"/"with" joining several names in one
    // author field.
    private static readonly System.Text.RegularExpressions.Regex MultiAuthorSeparator = new(
        @"\s*(?:;|&|/|\s+(?:and|AND|And|with)\s+)\s*",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    // Walks ancestors of `folderPath` upward (exclusive of `sourceRoot`) and
    // returns the first tracked or OL-matched entry. Skips the __unknown
    // quarantine folder so files already parked under it don't self-match to
    // a literal author named "__unknown".
    public AuthorIndexEntry? ResolveFolderAncestor(string folderPath, string sourceRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(sourceRoot);
        var current = Path.TrimEndingDirectorySeparator(folderPath);
        while (!string.IsNullOrWhiteSpace(current)
            && !string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(current);
            if (!string.IsNullOrWhiteSpace(name)
                && !string.Equals(name, CalibreScanner.UnknownAuthorFolder, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var probe in AuthorKeyVariants(name))
                    if (_index.TryGetValue(probe, out var hit)) return hit;
            }
            current = Path.GetDirectoryName(current);
        }
        return null;
    }

    // Same ancestor walk as ResolveFolderAncestor, but also returns the
    // nearest descendant folder name (the one between the author match and
    // `folderPath`). That's the effective title folder in a Calibre
    // <Author>/<Title>/... layout. Returns (null, null) on no match.
    public (AuthorIndexEntry? Entry, string? Title) ResolveFolderLayout(string folderPath, string sourceRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(sourceRoot);
        var current = Path.TrimEndingDirectorySeparator(folderPath);
        string? nearestBelow = null;

        while (!string.IsNullOrWhiteSpace(current)
            && !string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(current);
            if (!string.IsNullOrWhiteSpace(name)
                && !string.Equals(name, CalibreScanner.UnknownAuthorFolder, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var probe in AuthorKeyVariants(name))
                    if (_index.TryGetValue(probe, out var hit)) return (hit, nearestBelow);
                nearestBelow = name;
            }
            current = Path.GetDirectoryName(current);
        }
        return (null, null);
    }

    // Metadata author wins; falls back to the LEFT half of "{Author} - {Title}.ext".
    // When the left half contains an unclosed "(" — a sign the filename was
    // truncated at the OS path-length limit mid-series-annotation — the RIGHT
    // half (after the last " - ") is the author instead.
    // Sort form is kept independent so "Smith, John" can probe even when the
    // primary author is already in "John Smith" form.
    private static readonly System.Text.RegularExpressions.Regex TruncatedLeftParen = new(
        @"\([^)]{3,}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static (string? Primary, string? Sort) ExtractKeys(string? metadataAuthor, string? metadataAuthorSort, string filePath)
    {
        string? rawAuthor = metadataAuthor;
        if (string.IsNullOrWhiteSpace(rawAuthor))
        {
            var stem = Path.GetFileNameWithoutExtension(filePath);
            var dash = stem.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0)
            {
                var leftCandidate = stem[..dash].Trim();
                // Truncated title? E.g. "Betrayal of Innocence (A New Ad - Rebecca King"
                // — the unclosed "(" means the series annotation was cut off. Treat
                // the rightmost " - " segment as the author, not the left side.
                if (TruncatedLeftParen.IsMatch(leftCandidate))
                {
                    var lastDash = stem.LastIndexOf(" - ", StringComparison.Ordinal);
                    rawAuthor = lastDash > 0 ? stem[(lastDash + 3)..].Trim() : leftCandidate;
                }
                else
                {
                    rawAuthor = leftCandidate;
                }
            }
        }
        var a = string.IsNullOrWhiteSpace(rawAuthor) ? null : TitleNormalizer.NormalizeAuthor(rawAuthor);
        var s = string.IsNullOrWhiteSpace(metadataAuthorSort) ? null : TitleNormalizer.NormalizeAuthor(metadataAuthorSort);
        return (a, s);
    }

    private static (string Title, string Author)? TryReverseFilename(string filePath)
    {
        var stem = Path.GetFileNameWithoutExtension(filePath);
        var dash = stem.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash <= 0) return null;
        var left = stem[..dash].Trim();
        var right = stem[(dash + 3)..].Trim();
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return null;
        return (left, right);
    }

    private static IEnumerable<string> CandidateKeys(string? authorKey, string? authorSortKey)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in new[] { authorKey, authorSortKey })
        {
            if (string.IsNullOrWhiteSpace(k)) continue;
            foreach (var v in ExpandNameVariants(k))
                if (seen.Add(v)) yield return v;
        }
    }

    public static IEnumerable<string> AuthorKeyVariants(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;
        var normalized = TitleNormalizer.NormalizeAuthor(raw);
        foreach (var v in ExpandNameVariants(normalized)) yield return v;
    }

    // Yields the normalized name plus last-token-to-front and first-token-to-
    // back rotations, an initials-run split, and a first-name-to-initial form.
    // For two-token names both rotations collapse to the same reversed string;
    // three-plus token names cover both "First [Middle] Last" and
    // "Last First [Middle]" layouts.
    public static IEnumerable<string> ExpandNameVariants(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized)) yield break;
        var seen = new HashSet<string>(StringComparer.Ordinal) { normalized };
        yield return normalized;

        var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) yield break;

        var lastFirst = parts[^1] + " " + string.Join(' ', parts[..^1]);
        if (seen.Add(lastFirst)) yield return lastFirst;

        if (parts.Length > 2)
        {
            var firstLast = string.Join(' ', parts[1..]) + " " + parts[0];
            if (seen.Add(firstLast)) yield return firstLast;
        }

        // "ae van vogt" — a leading initials RUN with the dots dropped can
        // never equal the catalogue's "a e van vogt"; space it out.
        if (parts[0].Length is 2 or 3 && parts[0].All(char.IsLetter))
        {
            var spacedRun = string.Join(' ', parts[0].ToCharArray()) + " " + string.Join(' ', parts[1..]);
            if (seen.Add(spacedRun)) yield return spacedRun;
        }

        // "lyman frank baum" — the catalogue often holds the initialed form
        // ("l frank baum"); try the first name as a bare initial.
        if (parts.Length > 2 && parts[0].Length > 2)
        {
            var initialed = parts[0][0] + " " + string.Join(' ', parts[1..]);
            if (seen.Add(initialed)) yield return initialed;
        }
    }
}

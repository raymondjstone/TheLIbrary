using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Services.Incoming;

// One indexed author — either a tracked watchlist entry or an OpenLibrary
// catalog row. FolderName is where a matched file goes:
//   - tracked: the Calibre folder already in use (e.g. author.CalibreFolderName)
//   - ol    : the OL author's display name, sanitized for quarantine grouping
public sealed record AuthorIndexEntry(string DisplayName, string FolderName, bool IsTracked);

public sealed record AuthorMatchResult(AuthorIndexEntry Entry, string? RewrittenTitle);

// Pure in-memory author name matcher. Handles metadata-author,
// "Author - Title.ext" filenames, reverse "Title - Author.ext" filenames, and
// folder-layout ancestor matching. The caller decides where the matched file
// ultimately goes based on IsTracked.
public sealed class AuthorMatcher
{
    private readonly Dictionary<string, AuthorIndexEntry> _index = new(StringComparer.Ordinal);

    public AuthorMatcher(IEnumerable<AuthorIndexEntry> entries)
    {
        // Tracked entries win any collision — they have routing intent
        // (a real folder on disk); OL entries are just reference data.
        var ordered = entries.OrderByDescending(e => e.IsTracked);
        foreach (var e in ordered)
        {
            foreach (var v in AuthorKeyVariants(e.DisplayName)) _index.TryAdd(v, e);
            foreach (var v in AuthorKeyVariants(e.FolderName)) _index.TryAdd(v, e);
        }
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
    // Returns null on total miss — caller then tries folder layout and finally
    // an OpenLibrary catalog lookup via GetProbeKeys.
    public AuthorMatchResult? Resolve(string? metadataAuthor, string? metadataAuthorSort, string filePath)
    {
        var (primaryKey, sortKey) = ExtractKeys(metadataAuthor, metadataAuthorSort, filePath);
        foreach (var probe in CandidateKeys(primaryKey, sortKey))
            if (_index.TryGetValue(probe, out var hit))
                return new AuthorMatchResult(hit, null);

        var reversed = TryReverseFilename(filePath);
        if (reversed is not null)
        {
            foreach (var probe in AuthorKeyVariants(reversed.Value.Author))
                if (_index.TryGetValue(probe, out var hit))
                    return new AuthorMatchResult(hit, reversed.Value.Title);
        }
        return null;
    }

    // The full list of normalized probe keys that Resolve would have tried,
    // in priority order (forward first, reverse-filename last). Exposed so an
    // external store (e.g. the OpenLibrary catalog) can be queried with the
    // same keys when in-memory resolution misses. Pairs each key with the
    // rewritten title to use if that key matches via reverse filename.
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
    }

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
    // Sort form is kept independent so "Smith, John" can probe even when the
    // primary author is already in "John Smith" form.
    private static (string? Primary, string? Sort) ExtractKeys(string? metadataAuthor, string? metadataAuthorSort, string filePath)
    {
        string? rawAuthor = metadataAuthor;
        if (string.IsNullOrWhiteSpace(rawAuthor))
        {
            var stem = Path.GetFileNameWithoutExtension(filePath);
            var dash = stem.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0) rawAuthor = stem[..dash].Trim();
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
    // back rotations. For two-token names both rotations collapse to the same
    // reversed string; three-plus token names cover both "First [Middle] Last"
    // and "Last First [Middle]" layouts.
    public static IEnumerable<string> ExpandNameVariants(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized)) yield break;
        yield return normalized;

        var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) yield break;

        var lastFirst = parts[^1] + " " + string.Join(' ', parts[..^1]);
        if (lastFirst != normalized) yield return lastFirst;

        if (parts.Length > 2)
        {
            var firstLast = string.Join(' ', parts[1..]) + " " + parts[0];
            if (firstLast != normalized && firstLast != lastFirst) yield return firstLast;
        }
    }
}

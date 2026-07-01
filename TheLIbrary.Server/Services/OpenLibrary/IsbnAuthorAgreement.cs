using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Incoming;

namespace TheLibrary.Server.Services.OpenLibrary;

// Shared test for "is this ISBN's OpenLibrary work actually by that author?".
// Used by the Identified apply paths (which keep the folder author and never
// re-parent, so a wrong-author ISBN must be refused rather than mis-filed) and
// by the Identified page's ISBN-title preview (to warn when the resolved title
// wouldn't actually be applied because its author disagrees).
public static class IsbnAuthorAgreement
{
    // True when the OpenLibrary work reached via a scanned ISBN is by the given
    // author — matched by OL author key when both sides carry one (tolerating a
    // "/authors/" prefix), else by normalized name variants (the same matcher
    // used for incoming files, so "Last, First", initials and rotations agree).
    // A work OL lists with NO author can't be confirmed and is a NON-match.
    public static bool Matches(WorkSearchDoc doc, Author folderAuthor)
    {
        var mineKey = StripAuthorKeyPrefix(folderAuthor.OpenLibraryKey);
        if (!string.IsNullOrEmpty(mineKey) && doc.AuthorKeys is { Count: > 0 }
            && doc.AuthorKeys.Any(k => string.Equals(StripAuthorKeyPrefix(k), mineKey, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (doc.AuthorNames is not { Count: > 0 }) return false;
        var mine = new HashSet<string>(AuthorMatcher.AuthorKeyVariants(folderAuthor.Name), StringComparer.Ordinal);
        if (mine.Count == 0) return false;
        return doc.AuthorNames.Any(n => AuthorMatcher.AuthorKeyVariants(n).Any(mine.Contains));
    }

    // Reduce an OL author key to its bare "OL…A" token so "/authors/OL123A" and
    // "OL123A" compare equal.
    private static string? StripAuthorKeyPrefix(string? key)
        => string.IsNullOrWhiteSpace(key)
            ? null
            : key.Trim().TrimStart('/').Replace("authors/", "", StringComparison.OrdinalIgnoreCase);
}

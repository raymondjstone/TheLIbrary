using TheLibrary.Server.Data.Models;

namespace TheLibrary.Server.Services.Sync;

// Computes the canonical on-disk folder name for an author, applying the
// name-collision rule: when two or more authors share a normalised name and
// are NOT linked to each other (neither parent/child nor sibling pen names of
// the same canonical), every member of the collision gets a disambiguating
// suffix derived from their OpenLibrary key — provided every member has one.
// Authors whose OL key is missing keep the bare name until the key is filled
// in (rename happens on the next refresh / migration run).
public static class AuthorFolderNameResolver
{
    // Returns the desired folder leaf for `author`. Caller is responsible for
    // any sanitisation of filesystem-illegal characters.
    public static string Resolve(Author author, IReadOnlyList<Author> allAuthors)
    {
        if (string.IsNullOrWhiteSpace(author.Name)) return author.Name ?? "";

        var nameKey = TitleNormalizer.NormalizeAuthor(author.Name);
        if (string.IsNullOrEmpty(nameKey)) return author.Name;

        var group = FindCollisionGroup(author, allAuthors, nameKey);
        if (group.Count < 2) return author.Name;

        // Skip the suffix when ANY member of the collision group lacks an OL key —
        // we'd otherwise produce inconsistent layouts for the same person across
        // runs as keys get filled in. The rename will pick this up on the next
        // refresh / migration after the missing keys land.
        if (group.Any(a => string.IsNullOrWhiteSpace(a.OpenLibraryKey)))
            return author.Name;

        return $"{author.Name}_{author.OpenLibraryKey}";
    }

    // Returns the colliding authors (including `author` itself) — i.e. every
    // author with the same normalised name that is NOT linked to `author`.
    // Exported so callers can iterate the group as a unit when renaming on disk.
    public static IReadOnlyList<Author> FindCollisionGroup(
        Author author, IReadOnlyList<Author> allAuthors)
    {
        var nameKey = TitleNormalizer.NormalizeAuthor(author.Name ?? "");
        if (string.IsNullOrEmpty(nameKey)) return new[] { author };
        return FindCollisionGroup(author, allAuthors, nameKey);
    }

    private static IReadOnlyList<Author> FindCollisionGroup(
        Author author, IReadOnlyList<Author> allAuthors, string nameKey)
    {
        var siblings = allAuthors
            .Where(a => a.Id != author.Id)
            .Where(a => TitleNormalizer.NormalizeAuthor(a.Name) == nameKey)
            .Where(a => !AreLinked(author, a))
            .ToList();
        if (siblings.Count == 0) return new[] { author };
        var group = new List<Author>(siblings.Count + 1) { author };
        group.AddRange(siblings);
        return group;
    }

    // Two authors count as "linked" when:
    //   - one points at the other via LinkedToAuthorId (either direction), OR
    //   - both point at the same canonical (sibling pen names / alternates).
    // In any of these cases the user has explicitly merged them and they
    // shouldn't be disambiguated.
    private static bool AreLinked(Author x, Author y)
    {
        if (x.LinkedToAuthorId == y.Id) return true;
        if (y.LinkedToAuthorId == x.Id) return true;
        if (x.LinkedToAuthorId is int xc && y.LinkedToAuthorId is int yc && xc == yc) return true;
        return false;
    }
}

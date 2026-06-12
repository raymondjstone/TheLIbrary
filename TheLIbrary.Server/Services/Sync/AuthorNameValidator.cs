using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Services.Calibre;

namespace TheLibrary.Server.Services.Sync;

// Catalogue validation for guessed author names. A guess (from embedded
// metadata, a filename split, or prose parsing) is only trusted when it names
// a real author: an OpenLibrary-catalogue match or an existing watchlist
// author — and never a blacklisted one. "Last, First" forms are checked in
// both orientations, since OPF dc:creator and libgen-style filenames carry
// either.
public static class AuthorNameValidator
{
    // Returns the validated form of the name (the input itself, or its
    // comma-inverted variant — whichever the catalogue knows), or null when
    // neither validates.
    public static async Task<string?> ValidateAsync(LibraryDbContext db, string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (var candidate in Candidates(name.Trim()))
        {
            var normalized = TitleNormalizer.NormalizeAuthor(candidate);
            if (string.IsNullOrWhiteSpace(normalized)) continue;
            if (await db.AuthorBlacklist.AsNoTracking().AnyAsync(b => b.NormalizedName == normalized, ct))
                continue;
            var known = await db.OpenLibraryAuthors.AsNoTracking().AnyAsync(o => o.NormalizedName == normalized, ct)
                     || await db.Authors.AsNoTracking().AnyAsync(a => a.Name == candidate, ct);
            if (known) return candidate;
        }
        return null;
    }

    private static IEnumerable<string> Candidates(string name)
    {
        // For "Last, First" forms prefer returning the display orientation —
        // that's what gets stored on the scan row and sent to OL searches.
        var comma = name.IndexOf(',');
        if (comma > 0 && comma < name.Length - 1)
            yield return $"{name[(comma + 1)..].Trim()} {name[..comma].Trim()}";
        yield return name;

        // "CS Lewis" — a run of initials with the dots dropped normalizes to
        // "cs lewis", which can never match the catalogue's "c s lewis". Try
        // the spaced-out form too.
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2 && words[0].Length is 2 or 3 && words[0].All(char.IsUpper))
            yield return $"{string.Join(' ', words[0].ToCharArray())} {string.Join(' ', words[1..])}";

        // OL often catalogues an author under an initial ("L. Frank Baum")
        // while the file spells the forename out ("Lyman Frank Baum") — try
        // initializing the first one, then two, given names.
        if (words.Length >= 3 && words[0].Length > 2)
        {
            yield return $"{words[0][0]} {string.Join(' ', words[1..])}";
            if (words[1].Length > 2)
                yield return $"{words[0][0]} {words[1][0]} {string.Join(' ', words[2..])}";
        }
    }
}

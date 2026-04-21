namespace TheLibrary.Server.Data.Models;

// An author name the user has banished from the watchlist. Populated when a
// tracked author is deleted (their files go back to incoming, their row goes
// here). Incoming processing and the Calibre scanner both consult this list
// and treat any hit as "author not found" — so files land in __unknown
// instead of silently resurrecting the deleted author.
public class AuthorBlacklist
{
    public int Id { get; set; }

    // Display name preserved for the UI — the form the user saw when they
    // deleted the author (or typed in manually).
    public string Name { get; set; } = string.Empty;

    // TitleNormalizer.NormalizeAuthor(Name). This is what the matcher
    // compares against — stored so the DB can enforce uniqueness and so
    // the blacklist lookup is a single indexed Contains() per run.
    public string NormalizedName { get; set; } = string.Empty;

    // The Calibre folder name the deleted author was using, if any. Kept
    // so re-imports that see the same folder layout can be filtered
    // separately from the display name.
    public string? FolderName { get; set; }

    public DateTime AddedAt { get; set; }

    // Optional free-text note so the user can remember *why* they
    // blacklisted someone months later.
    public string? Reason { get; set; }
}

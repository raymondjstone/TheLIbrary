namespace TheLibrary.Server.Data.Models;

// A row from the OpenLibrary authors bulk dump
// (https://openlibrary.org/data/ol_dump_authors_latest.txt.gz). Seeding this
// table gives the watchlist-add UI a fast local search corpus so we don't have
// to hammer the OL search API. Watchlist entries live in `Author`; this table
// is reference data only and is fully rebuilt on each reseed.
public class OpenLibraryAuthor
{
    public int Id { get; set; }
    public string OlKey { get; set; } = string.Empty;          // "OL123A"
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty; // lowercased, no diacritics, no punctuation
    public string? PersonalName { get; set; }
    public string? AlternateNames { get; set; }                // "; "-separated
    public string? BirthDate { get; set; }
    public string? DeathDate { get; set; }
    public DateTime ImportedAt { get; set; }
}

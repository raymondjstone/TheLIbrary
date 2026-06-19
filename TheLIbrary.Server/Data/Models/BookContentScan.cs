using System.ComponentModel.DataAnnotations;

namespace TheLibrary.Server.Data.Models;

// Result of reading an unmatched/untracked file's front matter to guess what it
// is. One row per file path; its existence (at the recorded size/mtime) is also
// the "already scanned — don't scan again" marker. Kept in its own table so it
// survives the UnknownFiles re-index that sync performs.
public class BookContentScan
{
    public int Id { get; set; }

    [MaxLength(2048)]
    public string FullPath { get; set; } = "";

    public long SizeBytes { get; set; }
    public DateTime ModifiedAt { get; set; }

    // "unmatched" (a LocalBookFile with no book) or "untracked" (UnknownFiles).
    [MaxLength(20)]
    public string Source { get; set; } = "";

    // The author this file was linked to when scanned, if any (unmatched files).
    public int? AuthorId { get; set; }

    // --- What the front-matter extractor determined (any may be null) ---------
    [MaxLength(20)] public string? Isbn { get; set; }
    [MaxLength(500)] public string? Title { get; set; }
    [MaxLength(500)] public string? Author { get; set; }
    [MaxLength(500)] public string? Series { get; set; }
    [MaxLength(50)] public string? SeriesPosition { get; set; }
    // Semicolon-separated "also by this author" titles found in the front matter.
    [MaxLength(2000)] public string? AlsoByTitles { get; set; }

    // The series-grouped bibliography (series name, genre, titles) serialised as
    // JSON — an array of {Series,Genre,Titles[]}. This is what the series
    // catalogue is built from. Null when no grouped bibliography was found.
    public string? SeriesCatalogJson { get; set; }

    public DateTime ScannedAt { get; set; }

    // Set once the user has eyeballed this guess on the Identified Books page.
    // Reviewed rows drop off that list but keep the row (so the file isn't
    // re-scanned).
    public bool Reviewed { get; set; }

    // When the "assign untracked books to authors" job last TRIED to file this row
    // and could not resolve an author (no usable ISBN/title/author, author not on
    // OpenLibrary, …). Such rows rarely fix themselves, so once attempted they are
    // skipped on later runs instead of re-querying OpenLibrary every 15 minutes
    // forever. Null = never attempted (still eligible). Cleared in bulk from the
    // Settings page ("reset assign-attempt flags") to force a full re-run — e.g.
    // after new authors have been added that might now match.
    public DateTime? AssignAttemptedAt { get; set; }
}

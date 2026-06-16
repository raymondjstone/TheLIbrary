using System.ComponentModel.DataAnnotations;

namespace TheLibrary.Server.Data.Models;

public enum AuthorStatus
{
    Pending = 0,
    Active = 1,
    Excluded = 2,
    NotFound = 3
}

public class Author
{
    public int Id { get; set; }

    [MaxLength(32)]
    public string? OpenLibraryKey { get; set; }

    [MaxLength(512)]
    public string Name { get; set; } = "";

    [MaxLength(512)]
    public string? CalibreFolderName { get; set; }

    public AuthorStatus Status { get; set; } = AuthorStatus.Pending;

    [MaxLength(512)]
    public string? ExclusionReason { get; set; }

    public int? WorkCount { get; set; }

    // 0–5 priority rating the user assigns in the UI. Zero is a deliberate
    // rating ("lowest"), not "unrated" — the user confirmed all six values
    // are meaningful. Defaults to 0 on creation.
    public int Priority { get; set; }

    public DateTime? LastSyncedAt { get; set; }

    // When null the author is due immediately (treated as a new author).
    // After a successful fetch this is set to LastSyncedAt + an interval
    // derived from the author's most recent publication year, or the fixed
    // override below if the user has set one.
    public DateTime? NextFetchAt { get; set; }

    // Optional fixed refresh cadence in days set by the user. When null the
    // cadence is calculated from the author's most recent publication year.
    public int? RefreshIntervalDays { get; set; }

    // Stamped after each library file-matching pass. Used to order authors so
    // the longest-waiting (null = never scanned) are processed first each run.
    public DateTime? CalibreScannedAt { get; set; }

    public string? Bio { get; set; }

    // Free-text memo written by the user — not synced from OL, never overwritten.
    public string? Notes { get; set; }

    // When true, the AuthorRefresher fires a Pushover notification for each
    // newly-discovered book by this author (skipping the very first refresh
    // and skipping works whose FirstPublishYear pre-dates "recent"). Requires
    // PushoverAppToken / PushoverUserKey to be configured in AppSettings.
    public bool NotifyOnNewBooks { get; set; }

    // When set, this row is a duplicate (or pen name) of the referenced
    // author. OpenLibrary often splits one real author into several entries —
    // linking lets us treat them as one. The "canonical" author is the one
    // this points at; their LinkedToAuthorId is null.
    //   IsPenName == false → child's books fold into canonical's view AND files
    //                        get physically moved into canonical's folder.
    //   IsPenName == true  → child stays a separate author with its own books
    //                        and files; UI just shows a "pen name of" link
    //                        back to the canonical.
    public int? LinkedToAuthorId { get; set; }
    public Author? LinkedTo { get; set; }
    public bool IsPenName { get; set; }

    // When this author row was first created. DB-defaulted (SYSUTCDATETIME) so
    // every insert — from any service, job, or migration — is stamped without
    // each call site having to set it. Existing rows backfill to the migration
    // time; going forward it's an accurate creation/audit trail.
    public DateTime CreatedAt { get; set; }

    public List<Book> Books { get; set; } = new();
    public List<SeriesAuthor> SeriesAuthors { get; set; } = new();
    public List<Author> LinkedFrom { get; set; } = new();
}

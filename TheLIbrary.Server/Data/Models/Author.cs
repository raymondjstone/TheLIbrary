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
    // derived from the author's most recent publication year.
    public DateTime? NextFetchAt { get; set; }

    public List<Book> Books { get; set; } = new();
}

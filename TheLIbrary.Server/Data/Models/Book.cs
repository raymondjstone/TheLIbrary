using System.ComponentModel.DataAnnotations;

namespace TheLibrary.Server.Data.Models;

public enum ReadStatus
{
    Unread = 0,
    Reading = 1,
    Read = 2,
    Dnf = 3
}

public class Book
{
    public int Id { get; set; }

    [MaxLength(32)]
    public string OpenLibraryWorkKey { get; set; } = "";

    [MaxLength(1024)]
    public string Title { get; set; } = "";

    [MaxLength(1024)]
    public string? NormalizedTitle { get; set; }

    public int? FirstPublishYear { get; set; }

    public int? CoverId { get; set; }

    // Custom cover image URL, set by hand for manually-added books that have no
    // OpenLibrary cover. When present the UI prefers it over CoverId.
    [MaxLength(1024)]
    public string? CoverUrl { get; set; }

    public bool ManuallyOwned { get; set; }

    public DateTime? ManuallyOwnedAt { get; set; }

    // Semicolon-separated OL subject tags (e.g. "Science fiction;Fiction;Space opera (Fiction)")
    public string? Subjects { get; set; }

    public int? SeriesId { get; set; }
    public Series? Series { get; set; }

    [MaxLength(50)]
    public string? SeriesPosition { get; set; }

    public ReadStatus ReadStatus { get; set; } = ReadStatus.Unread;

    public DateTime? ReadAt { get; set; }

    public bool Wanted { get; set; }

    // User-hidden book. The author detail page renders these in a collapsed
    // bucket at the very bottom (still toggleable back on). Use for OL works
    // that are obviously non-English, duplicates, or just unwanted clutter
    // the user doesn't want OL refreshes to keep surfacing.
    public bool Suppressed { get; set; }

    // Title looks like it is not in English (set by the language-guess scan or
    // by hand on the Foreign Titles page). Foreign books are always Suppressed
    // too, so they drop out of the normal author/missing views; this separate
    // flag is what the Foreign Titles page lists and lets the user reverse.
    public bool Foreign { get; set; }

    // ISBN-13 (preferred) or ISBN-10, when known from imported metadata. Used
    // as a high-confidence match key in addition to NormalizedTitle.
    [MaxLength(20)]
    public string? Isbn { get; set; }

    public int AuthorId { get; set; }
    public Author Author { get; set; } = null!;

    public List<LocalBookFile> LocalFiles { get; set; } = new();
}

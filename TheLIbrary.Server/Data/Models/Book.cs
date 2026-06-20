using System.ComponentModel.DataAnnotations;

namespace TheLibrary.Server.Data.Models;

public enum ReadStatus
{
    Unread = 0,
    Reading = 1,
    Read = 2,
    Dnf = 3
}

// Tracks a user decision about a book's language, which overrides the
// automatic guessers (title scan + OpenLibrary language).
public enum LanguageReview
{
    // No human decision yet — the auto guessers are free to flag this book.
    None = 0,
    // User confirmed the book really is foreign. Stays foreign + suppressed;
    // shown at the bottom of the Foreign Titles list as already-reviewed.
    ConfirmedForeign = 1,
    // User confirmed the book is English. Permanently excluded from the foreign
    // scan and never suppressed for language — a sticky "leave this alone".
    ConfirmedEnglish = 2,
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

    // "Got but in a different edition" — the user already has this work as some
    // other edition than what's catalogued here (no local file matched). A third
    // ownership state alongside ebook (has LocalBookFile) and physical
    // (ManuallyOwned): it counts as owned everywhere, so the book drops off the
    // Missing / Wanted / unowned views, but it's shown with a distinct label
    // because there's no actual file to open here.
    public bool OwnedDifferentEdition { get; set; }

    public DateTime? OwnedDifferentEditionAt { get; set; }

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

    // The user's explicit language decision, if any. ConfirmedEnglish makes the
    // book immune to the foreign scan; ConfirmedForeign marks it reviewed.
    public LanguageReview LanguageReview { get; set; } = LanguageReview.None;

    // ISBN-13 (preferred) or ISBN-10, when known from imported metadata. Used
    // as a high-confidence match key in addition to NormalizedTitle.
    [MaxLength(20)]
    public string? Isbn { get; set; }

    // When this book row was first added to the library. DB-defaulted
    // (SYSUTCDATETIME) so every insert is stamped without each call site setting
    // it. Nullable: rows that predate this column stay null ("added before
    // tracking"); only genuinely-new books get a date, which is what the Recent
    // Releases "by month" grouping uses to surface what's actually new.
    public DateTime? CreatedAt { get; set; }

    // The CreatedAt to stamp on a book the library is seeing for the FIRST time.
    // A book whose publish year is already in the PAST was not really "added"
    // today — filing it under the current month would wrongly surface a years-old
    // title as a brand-new release. Date it to 1 Jan of its publish year so the
    // Recent Releases "by month" grouping reflects when the book actually came
    // out. A book published THIS year (or with an unknown/future year) returns
    // null, which lets the DB default (SYSUTCDATETIME) stamp the live insert time
    // — it genuinely is new to the library right now. Mirrors the one-time
    // BackfillBookCreatedAtFromPublishYear migration for new rows going forward.
    public static DateTime? CreatedAtForPublishYear(int? firstPublishYear)
        => firstPublishYear is int y && y >= 1 && y < DateTime.UtcNow.Year
            ? new DateTime(y, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            : null;

    public int AuthorId { get; set; }
    public Author Author { get; set; } = null!;

    public List<LocalBookFile> LocalFiles { get; set; } = new();
    public List<BookCollection> Collections { get; set; } = new();
}

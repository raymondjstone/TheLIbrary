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

    public bool ManuallyOwned { get; set; }

    public DateTime? ManuallyOwnedAt { get; set; }

    // Semicolon-separated OL subject tags (e.g. "Science fiction;Fiction;Space opera (Fiction)")
    public string? Subjects { get; set; }

    [MaxLength(512)]
    public string? Series { get; set; }

    [MaxLength(50)]
    public string? SeriesPosition { get; set; }

    public ReadStatus ReadStatus { get; set; } = ReadStatus.Unread;

    public DateTime? ReadAt { get; set; }

    public bool Wanted { get; set; }

    public int AuthorId { get; set; }
    public Author Author { get; set; } = null!;

    public List<LocalBookFile> LocalFiles { get; set; } = new();
}

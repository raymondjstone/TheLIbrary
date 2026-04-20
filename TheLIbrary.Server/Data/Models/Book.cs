using System.ComponentModel.DataAnnotations;

namespace TheLibrary.Server.Data.Models;

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

    public int AuthorId { get; set; }
    public Author Author { get; set; } = null!;

    public List<LocalBookFile> LocalFiles { get; set; } = new();
}

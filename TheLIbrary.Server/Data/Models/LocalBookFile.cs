using System.ComponentModel.DataAnnotations;

namespace TheLibrary.Server.Data.Models;

public class LocalBookFile
{
    public int Id { get; set; }

    [MaxLength(1024)]
    public string AuthorFolder { get; set; } = "";

    [MaxLength(1024)]
    public string TitleFolder { get; set; } = "";

    [MaxLength(2048)]
    public string FullPath { get; set; } = "";

    [MaxLength(1024)]
    public string? NormalizedTitle { get; set; }

    public int? BookId { get; set; }
    public Book? Book { get; set; }

    public int? AuthorId { get; set; }
    public Author? Author { get; set; }

    public DateTime LastSeenAt { get; set; }

    // Fingerprint of the title folder's contents on disk; lets us skip
    // metadata reads + rematching when nothing has changed since last scan.
    public long SizeBytes { get; set; }
    public DateTime ModifiedAt { get; set; }

    // Pulled from the first .epub in the folder when the fingerprint changes.
    [MaxLength(500)]
    public string? MetadataTitle { get; set; }
    [MaxLength(500)]
    public string? MetadataAuthor { get; set; }
    [MaxLength(20)]
    public string? MetadataLanguage { get; set; }
}

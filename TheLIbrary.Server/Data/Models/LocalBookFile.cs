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

    // Set by UnmatchLocalFile; tells the sync scanner not to auto-re-match
    // this file. Cleared when the user manually assigns it to a book.
    public bool ManuallyUnmatched { get; set; }

    public int? AuthorId { get; set; }
    public Author? Author { get; set; }

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
    // ISBN-13 (preferred) or ISBN-10 extracted from EPUB/OPF dc:identifier when
    // present. Lets the matcher hit a book by identifier even if titles diverge.
    [MaxLength(20)]
    public string? Isbn { get; set; }

    // Comma-separated list of additional Book.Id values this file ALSO
    // represents (omnibus / boxed-set support). BookId remains the "primary"
    // book in the file; AdditionalBookIds extends ownership without breaking
    // the existing single-FK navigation property.
    [MaxLength(500)]
    public string? AdditionalBookIds { get; set; }
}

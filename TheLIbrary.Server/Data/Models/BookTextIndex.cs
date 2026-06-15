namespace TheLibrary.Server.Data.Models;

// Extracted text of a matched ebook, for the optional full-text search feature.
// One row per Book (the best/most-recently-indexed local file). Content is the
// head of the book's readable text (capped); SizeBytes/ModifiedAt let the
// indexer skip files that haven't changed since the last pass.
public class BookTextIndex
{
    public int Id { get; set; }

    public int BookId { get; set; }
    public Book Book { get; set; } = null!;

    public int? AuthorId { get; set; }

    // The local file the text was extracted from.
    public string FullPath { get; set; } = "";

    // Head of the readable text (capped). nvarchar(max).
    public string Content { get; set; } = "";

    public long SizeBytes { get; set; }
    public DateTime ModifiedAt { get; set; }
    public DateTime IndexedAt { get; set; }
}

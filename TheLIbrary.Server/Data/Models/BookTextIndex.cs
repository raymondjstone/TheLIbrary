namespace TheLibrary.Server.Data.Models;

// Where an indexed text row came from.
public enum TextIndexSource
{
    MatchedBook = 0,          // a local file linked to a Book
    UnmatchedAuthorFile = 1,  // a file in an author folder not matched to a Book
    UnknownFile = 2,          // a loose file in the __unknown quarantine
}

// Extracted text of an indexed file, for the optional full-text search feature.
// One row per file (keyed by FullPath). Content is the head of the file's
// readable text (capped); SizeBytes/ModifiedAt let the indexer skip files that
// haven't changed since the last pass.
public class BookTextIndex
{
    public int Id { get; set; }

    // The local file the text was extracted from (unique).
    public string FullPath { get; set; } = "";

    public TextIndexSource Source { get; set; }

    // Set for MatchedBook rows; null for unmatched / unknown files.
    public int? BookId { get; set; }
    public Book? Book { get; set; }

    public int? AuthorId { get; set; }

    // Display title: the book title for matched rows, otherwise the title folder
    // / filename stem so unmatched + unknown hits are still readable in results.
    public string Title { get; set; } = "";

    // Head of the readable text (capped). nvarchar(max).
    public string Content { get; set; } = "";

    public long SizeBytes { get; set; }
    public DateTime ModifiedAt { get; set; }
    public DateTime IndexedAt { get; set; }
}

using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class SyncServiceMatchingTests
{
    [Fact]
    public void MatchAuthorFilesForTests_Matches_Book_From_Stripped_Author_Prefix()
    {
        var author = new Author { Id = 1, Name = "Terry Brooks" };
        var entries = new Dictionary<string, List<CalibreBookEntry>>(StringComparer.Ordinal)
        {
            [TitleNormalizer.NormalizeAuthor("Terry Brooks")] =
            [new("C:\\lib", "Terry Brooks", "Terry Brooks - Magic Kingdom for Sale", "C:\\lib\\Terry Brooks\\book.epub", 1, DateTime.UtcNow)]
        };
        var books = new Dictionary<int, List<Book>>
        {
            [1] = [new Book { Id = 10, AuthorId = 1, Title = "Magic Kingdom for Sale", NormalizedTitle = TitleNormalizer.Normalize("Magic Kingdom for Sale"), OpenLibraryWorkKey = "OL10W" }]
        };

        var result = SyncService.MatchAuthorFilesForTests(author, entries, books, new Dictionary<int, List<int>>(), new Dictionary<string, LocalBookFile>(StringComparer.Ordinal));

        var insert = Assert.Single(result.Inserts);
        Assert.Equal(10, insert.BookId);
        Assert.Equal(1, insert.AuthorId);
    }

    [Fact]
    public void MatchAuthorFilesForTests_Uses_NonPenName_Child_Books_For_Canonical()
    {
        var author = new Author { Id = 1, Name = "Canonical" };
        var entries = new Dictionary<string, List<CalibreBookEntry>>(StringComparer.Ordinal)
        {
            [TitleNormalizer.NormalizeAuthor("Canonical")] =
            [new("C:\\lib", "Canonical", "Child Book", "C:\\lib\\Canonical\\child.epub", 1, DateTime.UtcNow)]
        };
        var books = new Dictionary<int, List<Book>>
        {
            [2] = [new Book { Id = 20, AuthorId = 2, Title = "Child Book", NormalizedTitle = TitleNormalizer.Normalize("Child Book"), OpenLibraryWorkKey = "OL20W" }]
        };
        var children = new Dictionary<int, List<int>> { [1] = [2] };

        var result = SyncService.MatchAuthorFilesForTests(author, entries, books, children, new Dictionary<string, LocalBookFile>(StringComparer.Ordinal));

        var insert = Assert.Single(result.Inserts);
        Assert.Equal(20, insert.BookId);
        Assert.Equal(1, insert.AuthorId);
    }

    [Fact]
    public void MatchAuthorFilesForTests_Never_Auto_Links_To_A_Suppressed_Or_Foreign_Book()
    {
        // The user suppressed/flagged a junk author-prefixed book so it vanished
        // from the visible list. A file whose stem matches that junk title must NOT
        // be auto-linked to it — a hidden book is never a valid match target.
        var author = new Author { Id = 1, Name = "Alan Dean Foster" };
        var entries = new Dictionary<string, List<CalibreBookEntry>>(StringComparer.Ordinal)
        {
            [TitleNormalizer.NormalizeAuthor("Alan Dean Foster")] =
            [new("C:\\lib", "Alan Dean Foster", "Alan Dean Foster - Alien - Ala", "C:\\lib\\Alan Dean Foster\\junk.epub", 1, DateTime.UtcNow)]
        };
        var books = new Dictionary<int, List<Book>>
        {
            [1] =
            [
                new Book { Id = 10, AuthorId = 1, Title = "Alan Dean Foster - Alien - Ala", NormalizedTitle = TitleNormalizer.Normalize("Alan Dean Foster - Alien - Ala"), OpenLibraryWorkKey = "OL10W", Suppressed = true },
                new Book { Id = 11, AuthorId = 1, Title = "Alan Dean Foster - Alien", NormalizedTitle = TitleNormalizer.Normalize("Alan Dean Foster - Alien"), OpenLibraryWorkKey = "OL11W", Foreign = true },
            ]
        };

        var result = SyncService.MatchAuthorFilesForTests(author, entries, books, new Dictionary<int, List<int>>(), new Dictionary<string, LocalBookFile>(StringComparer.Ordinal));

        var insert = Assert.Single(result.Inserts);
        Assert.Null(insert.BookId);     // left unmatched, not linked to the hidden junk book
        Assert.Equal(1, insert.AuthorId);
    }

    [Fact]
    public void MatchAuthorFilesForTests_Updates_Existing_Row_Without_Overwriting_Manual_Unmatch()
    {
        var author = new Author { Id = 1, Name = "Author" };
        var entry = new CalibreBookEntry("C:\\lib", "Author", "Author - Title", "C:\\lib\\Author\\title.epub", 2, DateTime.UtcNow);
        var existing = new LocalBookFile
        {
            Id = 5,
            FullPath = entry.FullPath,
            AuthorFolder = entry.AuthorFolder,
            TitleFolder = entry.TitleFolder,
            SizeBytes = 1,
            ModifiedAt = DateTime.MinValue,
            ManuallyUnmatched = true
        };
        var existingByPath = new Dictionary<string, LocalBookFile>(StringComparer.Ordinal)
        {
            [entry.FullPath.Normalize(System.Text.NormalizationForm.FormC).ToUpperInvariant()] = existing
        };
        var entries = new Dictionary<string, List<CalibreBookEntry>>(StringComparer.Ordinal)
        {
            [TitleNormalizer.NormalizeAuthor("Author")] = [entry]
        };
        var books = new Dictionary<int, List<Book>>
        {
            [1] = [new Book { Id = 11, AuthorId = 1, Title = "Title", NormalizedTitle = TitleNormalizer.Normalize("Title"), OpenLibraryWorkKey = "OL11W" }]
        };

        var result = SyncService.MatchAuthorFilesForTests(author, entries, books, new Dictionary<int, List<int>>(), existingByPath);

        var update = Assert.Single(result.Updates);
        Assert.Null(update.BookId);
        Assert.Equal(1, update.AuthorId);
    }
}

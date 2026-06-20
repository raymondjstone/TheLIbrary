using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using TheLibrary.Server.Controllers;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

[Collection("Integration")]
public class BooksControllerIntegrationTests
{
    [Fact]
    public async Task Update_Returns_BadRequest_For_Implausible_Year()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "Author" });
            db.Books.Add(new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL1W", Title = "Book", NormalizedTitle = "book" });
        });

        using var client = factory.CreateClient();
        var response = await client.PutAsJsonAsync("/api/books/10", new BooksController.UpdateBookRequest(null, DateTime.UtcNow.Year + 10, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Update_Reassigns_Author_When_Target_Is_Valid_And_No_Clash()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db =>
        {
            db.Authors.AddRange(
                new Author { Id = 1, Name = "Author One" },
                new Author { Id = 2, Name = "Author Two" });
            db.Books.Add(new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL1W", Title = "Book", NormalizedTitle = "book" });
        });

        using var client = factory.CreateClient();
        var response = await client.PutAsJsonAsync("/api/books/10", new BooksController.UpdateBookRequest("Updated", 1999, 2));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var book = await db.Books.FindAsync(10);
        Assert.Equal(2, book!.AuthorId);
        Assert.Equal("Updated", book.Title);
        Assert.Equal(1999, book.FirstPublishYear);
    }

    [Fact]
    public async Task Update_Returns_Conflict_When_Target_Author_Already_Has_Work()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db =>
        {
            db.Authors.AddRange(
                new Author { Id = 1, Name = "Author One" },
                new Author { Id = 2, Name = "Author Two" });
            db.Books.AddRange(
                new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL1W", Title = "Book", NormalizedTitle = "book" },
                new Book { Id = 11, AuthorId = 2, OpenLibraryWorkKey = "OL1W", Title = "Same Work", NormalizedTitle = "same work" });
        });

        using var client = factory.CreateClient();
        var response = await client.PutAsJsonAsync("/api/books/10", new BooksController.UpdateBookRequest(null, null, 2));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Manual_Returns_Only_Manual_Books_With_Owned_State()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "Author" });
            db.Books.AddRange(
                new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "XX12345678W", Title = "Manual", NormalizedTitle = "manual", ManuallyOwned = true },
                new Book { Id = 11, AuthorId = 1, OpenLibraryWorkKey = "OL1W", Title = "Regular", NormalizedTitle = "regular" });
        });

        using var client = factory.CreateClient();
        var rows = await client.GetFromJsonAsync<List<BooksController.ManualBookRow>>("/api/books/manual");

        var row = Assert.Single(rows!);
        Assert.Equal(10, row.Id);
        Assert.True(row.Owned);
    }

    [Fact]
    public async Task SetCover_Clears_CoverUrl_When_Empty_String_Posted()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "Author" });
            db.Books.Add(new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL1W", Title = "Book", NormalizedTitle = "book", CoverUrl = "https://example.com/cover.jpg" });
        });

        using var client = factory.CreateClient();
        var response = await client.PutAsJsonAsync("/api/books/10/cover", new BooksController.SetCoverRequest { Url = "" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        Assert.Null((await db.Books.FindAsync(10))!.CoverUrl);
    }

    [Fact]
    public async Task BulkSetOwnership_Requires_Ids()
    {
        using var factory = new LibraryApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/books/bulk-ownership", new BooksController.BulkOwnershipRequest(Array.Empty<int>(), true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Duplicates_Keeper_Is_Never_A_Damaged_Copy()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "Author" });
            db.Books.Add(new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL1W", Title = "Book", NormalizedTitle = "book" });
            // Preferred format (epub) is damaged; the healthy copy is a pdf.
            db.LocalBookFiles.AddRange(
                new LocalBookFile { Id = 1, BookId = 10, AuthorId = 1, FullPath = "/lib/Author/book.epub", IntegrityOk = false },
                new LocalBookFile { Id = 2, BookId = 10, AuthorId = 1, FullPath = "/lib/Author/book.pdf", IntegrityOk = true });
        });

        using var client = factory.CreateClient();
        var groups = await client.GetFromJsonAsync<List<BooksController.DuplicateGroup>>("/api/books/duplicates");

        var g = Assert.Single(groups!);
        Assert.Equal(2, g.RecommendedFileId);          // the healthy pdf, not the damaged epub
        Assert.Equal("pdf", g.RecommendedFormat);
        // Per-copy integrity status is surfaced.
        Assert.Equal(false, g.Files.Single(f => f.Id == 1).IntegrityOk);
        Assert.Equal(true, g.Files.Single(f => f.Id == 2).IntegrityOk);
    }

    [Fact]
    public async Task Duplicates_Ignores_Empty_Folder_Rows_But_Keeps_Folders_With_A_Book()
    {
        var root = Path.Combine(Path.GetTempPath(), "thelib-dup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var realFile = Path.Combine(root, "book.epub");
        await File.WriteAllBytesAsync(realFile, new byte[] { 1 });
        var emptyFolder = Path.Combine(root, "Empty Title Folder");
        Directory.CreateDirectory(emptyFolder); // a stale title-folder pointer, no ebook inside
        var folderWithBook = Path.Combine(root, "Title With Book");
        Directory.CreateDirectory(folderWithBook);
        await File.WriteAllBytesAsync(Path.Combine(folderWithBook, "inside.pdf"), new byte[] { 1 });

        try
        {
            using var factory = new LibraryApiFactory();
            await SeedAsync(factory, db =>
            {
                db.Authors.Add(new Author { Id = 1, Name = "A" });
                // Book 10: real file + an empty folder → only ONE real copy → not a duplicate.
                db.Books.Add(new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL1W", Title = "Has Empty", NormalizedTitle = "a" });
                db.LocalBookFiles.AddRange(
                    new LocalBookFile { Id = 1, BookId = 10, AuthorId = 1, FullPath = realFile },
                    new LocalBookFile { Id = 2, BookId = 10, AuthorId = 1, FullPath = emptyFolder });
                // Book 20: real file + a folder that holds an ebook → two real copies → a duplicate.
                db.Books.Add(new Book { Id = 20, AuthorId = 1, OpenLibraryWorkKey = "OL2W", Title = "Two Copies", NormalizedTitle = "b" });
                db.LocalBookFiles.AddRange(
                    new LocalBookFile { Id = 3, BookId = 20, AuthorId = 1, FullPath = realFile },
                    new LocalBookFile { Id = 4, BookId = 20, AuthorId = 1, FullPath = folderWithBook });
            });

            using var client = factory.CreateClient();
            var groups = await client.GetFromJsonAsync<List<BooksController.DuplicateGroup>>("/api/books/duplicates");

            Assert.DoesNotContain(groups!, g => g.BookId == 10); // empty folder is not a copy
            Assert.Contains(groups!, g => g.BookId == 20);       // folder-with-book counts
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task RecentReleases_Excludes_Foreign_Books()
    {
        var year = DateTime.UtcNow.Year;
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "Author", Priority = 1 });
            db.Books.AddRange(
                new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL1W", Title = "English Title", NormalizedTitle = "english title", FirstPublishYear = year },
                // Foreign but its Suppressed flag has drifted off — must still be
                // excluded from the releases pages on the Foreign flag alone.
                new Book { Id = 11, AuthorId = 1, OpenLibraryWorkKey = "OL2W", Title = "Foreign Title", NormalizedTitle = "foreign title", FirstPublishYear = year, Foreign = true, Suppressed = false });
        });

        using var client = factory.CreateClient();
        foreach (var url in new[] { "/api/books/recent-releases", "/api/books/recent-releases/all" })
        {
            var rows = await client.GetFromJsonAsync<List<BooksController.RecentReleaseRow>>(url);
            Assert.NotNull(rows);
            Assert.Contains(rows!, r => r.Id == 10);
            Assert.DoesNotContain(rows!, r => r.Id == 11);
        }
    }

    private static async Task SeedAsync(LibraryApiFactory factory, Action<LibraryDbContext> seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }
}

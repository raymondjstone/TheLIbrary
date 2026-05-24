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
public class AuthorsControllerIntegrationTests
{
    [Fact]
    public async Task SetPriority_Rejects_Out_Of_Range_Value()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db => db.Authors.Add(new Author { Id = 1, Name = "Author" }));
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/authors/1/priority", new AuthorsController.SetPriorityRequest(6));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SetPriority_Updates_Author()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db => db.Authors.Add(new Author { Id = 1, Name = "Author", Priority = 0 }));
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/authors/1/priority", new AuthorsController.SetPriorityRequest(4));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        Assert.Equal(4, (await db.Authors.FindAsync(1))!.Priority);
    }

    [Fact]
    public async Task SetRefreshInterval_Rejects_Invalid_Range()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db => db.Authors.Add(new Author { Id = 1, Name = "Author" }));
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/authors/1/refresh-interval", new AuthorsController.SetRefreshIntervalRequest(0));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SaveNotes_Trims_Text_And_Persists()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db => db.Authors.Add(new Author { Id = 1, Name = "Author" }));
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/authors/1/notes", new AuthorsController.SaveNotesRequest("  some notes  "));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        Assert.Equal("some notes", (await db.Authors.FindAsync(1))!.Notes);
    }

    [Fact]
    public async Task AddBook_Creates_Manual_Book_For_Author()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db => db.Authors.Add(new Author { Id = 1, Name = "Author" }));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/authors/1/books", new AuthorsController.AddBookRequest("Manual Title", 2005, "Saga", "1", true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var book = Assert.Single(db.Books.Where(b => b.AuthorId == 1));
        Assert.StartsWith("XX", book.OpenLibraryWorkKey, StringComparison.Ordinal);
        Assert.Equal("Manual Title", book.Title);
        Assert.True(book.ManuallyOwned);
    }

    [Fact]
    public async Task AddOpenLibraryBook_Creates_Work_For_Author()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db => db.Authors.Add(new Author { Id = 1, Name = "Author" }));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/authors/1/books/openlibrary",
            new AuthorsController.AddOpenLibraryBookRequest("/works/OL123W", "Open Work", 1986, 42, "Author", true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var book = Assert.Single(db.Books.Where(b => b.AuthorId == 1));
        Assert.Equal("OL123W", book.OpenLibraryWorkKey);
        Assert.Equal("Open Work", book.Title);
        Assert.True(book.ManuallyOwned);
    }

    [Fact]
    public async Task MatchLocalFileToOpenLibraryWork_Creates_Book_And_Matches_File()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "Author", OpenLibraryKey = "OL1A" });
            db.LocalBookFiles.Add(new LocalBookFile
            {
                Id = 10,
                AuthorId = 1,
                AuthorFolder = "Author",
                TitleFolder = "Open Work",
                FullPath = "C:\\Lib\\Author\\Open Work",
                ManuallyUnmatched = true,
            });
            db.LibraryLocations.Add(new LibraryLocation
            {
                Id = 1,
                Label = "Default",
                Path = "C:\\Lib",
                Enabled = true,
                CreatedAt = DateTime.UtcNow,
            });
        });
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/authors/1/unmatched/10/openlibrary-match",
            new AuthorsController.MatchOpenLibraryFileRequest("/works/OL123W", "Open Work", 1986, 42, "Author", "OL1A", "Author"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var book = Assert.Single(db.Books);
        var file = Assert.Single(db.LocalBookFiles);
        Assert.Equal(book.Id, file.BookId);
        Assert.False(file.ManuallyUnmatched);
    }

    [Fact]
    public async Task AddAuthor_Rejects_Blacklisted_Name_When_Name_Is_Provided()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db =>
        {
            db.AuthorBlacklist.Add(new AuthorBlacklist
            {
                Name = "Blocked Author",
                NormalizedName = TheLibrary.Server.Services.Sync.TitleNormalizer.NormalizeAuthor("Blocked Author")
            });
        });
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/authors", new AuthorsController.AddAuthorRequest
        {
            OpenLibraryKey = "OL1A",
            Name = "Blocked Author"
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Starred_Returns_Aggregated_Counts()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "Starred", Priority = 3 });
            db.Books.Add(new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W", Title = "Owned Ebook", NormalizedTitle = "owned ebook" });
            db.Books.Add(new Book { Id = 11, AuthorId = 1, OpenLibraryWorkKey = "OL11W", Title = "Physical", NormalizedTitle = "physical", ManuallyOwned = true });
            db.LocalBookFiles.Add(new LocalBookFile { Id = 50, AuthorId = 1, BookId = 10, AuthorFolder = "Starred", TitleFolder = "Owned Ebook", FullPath = "C:\\Lib\\Starred\\Owned Ebook\\book.epub" });
            db.LocalBookFiles.Add(new LocalBookFile { Id = 51, AuthorId = 1, BookId = null, AuthorFolder = "Starred", TitleFolder = "Unmatched", FullPath = "C:\\Lib\\Starred\\Unmatched\\book.epub" });
        });
        using var client = factory.CreateClient();

        var rows = await client.GetFromJsonAsync<List<AuthorsController.StarredAuthorRow>>("/api/authors/starred");

        var row = Assert.Single(rows!);
        Assert.Equal(2, row.BookCount);
        Assert.Equal(1, row.EbookCount);
        Assert.Equal(1, row.UnmatchedCount);
    }

    [Fact]
    public async Task Link_Rejects_Self_Link()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db => db.Authors.Add(new Author { Id = 1, Name = "Author" }));
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/authors/1/link", new AuthorsController.LinkAuthorRequest(1, false));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task SeedAsync(LibraryApiFactory factory, Action<LibraryDbContext> seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }
}

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

    private static async Task SeedAsync(LibraryApiFactory factory, Action<LibraryDbContext> seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }
}

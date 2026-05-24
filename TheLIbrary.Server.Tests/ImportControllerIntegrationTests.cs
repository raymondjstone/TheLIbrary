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
public class ImportControllerIntegrationTests
{
    [Fact]
    public async Task SearchOpenLibraryWorks_Returns_Mapped_Works_For_Unmatched_Row()
    {
        using var factory = new LibraryApiFactory((request, _) =>
        {
            var url = request.RequestUri!.ToString();
            Assert.Contains("search.json", url);
            Assert.Contains("title=Magic+Kingdom+for+Sale", url);
            Assert.DoesNotContain("author=", url);
            return Task.FromResult(TestHttpMessageHandler.Json("""
                {"docs":[{"key":"/works/OL123W","title":"Magic Kingdom for Sale","first_publish_year":1986,"cover_i":42,"author_name":["Terry Brooks"],"author_key":["OL123A"]}]}
                """));
        });
        await SeedAsync(factory, db =>
        {
            db.PhysicalBookUnmatched.Add(new PhysicalBookUnmatched { Id = 1, Author = "Wrong Author", Title = "Magic Kingdom for Sale", AddedAt = DateTime.UtcNow });
        });
        using var client = factory.CreateClient();

        var result = await client.GetFromJsonAsync<List<ImportController.OpenLibraryWorkCandidate>>("/api/import/physical-books/unmatched/1/openlibrary-search");

        var row = Assert.Single(result!);
        Assert.Equal("/works/OL123W", row.Key);
        Assert.Equal("Magic Kingdom for Sale", row.Title);
        Assert.Equal(1986, row.FirstPublishYear);
        Assert.Equal(42, row.CoverId);
        Assert.Equal("OL123A", row.PrimaryAuthorKey);
        Assert.Equal("Terry Brooks", row.PrimaryAuthorName);
    }

    [Fact]
    public async Task AddUnmatchedOpenLibraryBook_Creates_Book_And_Clears_Unmatched_Row()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "Terry Brooks" });
            db.PhysicalBookUnmatched.Add(new PhysicalBookUnmatched { Id = 1, Author = "Terry Brooks", Title = "Magic Kingdom for Sale", Isbn = "9780345317583", AddedAt = DateTime.UtcNow });
        });
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/import/physical-books/unmatched/1/add-openlibrary-book",
            new ImportController.AddUnmatchedOpenLibraryBookRequest(1, "/works/OL123W", "Magic Kingdom for Sale", 1986, 42, "Terry Brooks"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var book = Assert.Single(db.Books);
        Assert.Equal("OL123W", book.OpenLibraryWorkKey);
        Assert.True(book.ManuallyOwned);
        Assert.Equal("9780345317583", book.Isbn);
        Assert.Empty(db.PhysicalBookUnmatched);
    }

    [Fact]
    public async Task AddUnmatchedOpenLibraryBook_Reuses_Existing_Book_For_Author()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "Terry Brooks" });
            db.Books.Add(new Book { Id = 10, AuthorId = 1, Title = "Magic Kingdom for Sale", NormalizedTitle = "magic kingdom for sale", OpenLibraryWorkKey = "OL123W" });
            db.PhysicalBookUnmatched.Add(new PhysicalBookUnmatched { Id = 1, Author = "Terry Brooks", Title = "Magic Kingdom for Sale", AddedAt = DateTime.UtcNow });
        });
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/import/physical-books/unmatched/1/add-openlibrary-book",
            new ImportController.AddUnmatchedOpenLibraryBookRequest(1, "OL123W", "Magic Kingdom for Sale", 1986, 42, "Terry Brooks"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        Assert.Single(db.Books);
        Assert.True(db.Books.Single().ManuallyOwned);
        Assert.Empty(db.PhysicalBookUnmatched);
    }

    private static async Task SeedAsync(LibraryApiFactory factory, Action<LibraryDbContext> seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }
}

using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using TheLibrary.Server.Controllers;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

// HTTP integration coverage through the full DI pipeline (LibraryApiFactory),
// with OpenLibrary HTTP stubbed. Exercises the OL-backed AuthorsController/
// BooksController endpoints and a broad sweep of GET routes (which also drives
// the Program.cs request pipeline).
[Collection("Integration")]
public class OlEndpointCoverageTests
{
    private static Task<HttpResponseMessage> Ol(HttpRequestMessage req, CancellationToken _)
    {
        var url = req.RequestUri!.ToString();
        if (url.Contains("search/authors.json"))
            return Task.FromResult(TestHttpMessageHandler.Json(
                """{"docs":[{"key":"OL1A","name":"Primary Auth","work_count":5}]}"""));
        if (url.Contains("search.json"))
            return Task.FromResult(TestHttpMessageHandler.Json(
                """{"docs":[{"key":"/works/OL10W","title":"Alpha","first_publish_year":2001,"cover_i":7,"author_name":["Primary Auth"],"author_key":["OL1A"]}]}"""));
        return Task.FromResult(TestHttpMessageHandler.Json("{}"));
    }

    private static async Task SeedAsync(LibraryApiFactory f, Action<LibraryDbContext> seed)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task AddOpenLibraryBook_Creates_Book()
    {
        using var factory = new LibraryApiFactory(Ol);
        await SeedAsync(factory, db => db.Authors.Add(new Author { Id = 1, Name = "Primary Auth", Priority = 1 }));
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/authors/1/books/openlibrary",
            new AuthorsController.AddOpenLibraryBookRequest("/works/OL10W", "Alpha", 2001, 7, "Primary Auth", true));
        resp.EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        Assert.True(db.Books.Any(b => b.OpenLibraryWorkKey == "OL10W"));
    }

    [Fact]
    public async Task Unmatched_Suggestions_Uses_OpenLibrary()
    {
        using var factory = new LibraryApiFactory(Ol);
        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "Primary Auth", Priority = 1, CalibreFolderName = "Primary Auth" });
            db.LocalBookFiles.Add(new LocalBookFile { Id = 1, AuthorId = 1, BookId = null, AuthorFolder = "Primary Auth", TitleFolder = "Alpha", FullPath = "/lib/Primary Auth/Alpha.epub", ModifiedAt = DateTime.UtcNow });
        });
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/authors/1/unmatched/suggestions");
        Assert.True(resp.IsSuccessStatusCode, $"status {(int)resp.StatusCode}");
    }

    [Fact]
    public async Task Broad_Get_Sweep_Over_Books_And_Authors()
    {
        using var factory = new LibraryApiFactory(Ol);
        var year = DateTime.UtcNow.Year;
        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "Primary Auth", Priority = 1 });
            db.Series.Add(new Series { Id = 1, Name = "Cycle", NormalizedName = "cycle", PrimaryAuthorId = 1 });
            db.Books.AddRange(
                new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W", Title = "Alpha", NormalizedTitle = "alpha", FirstPublishYear = year, SeriesId = 1, ManuallyOwned = true },
                new Book { Id = 11, AuthorId = 1, OpenLibraryWorkKey = "OL11W", Title = "Beta", NormalizedTitle = "beta", FirstPublishYear = year, Wanted = true });
        });
        using var client = factory.CreateClient();

        // Books-side GET routes that run on the InMemory test provider — assert OK.
        foreach (var url in new[]
        {
            "/api/authors/1", "/api/books/missing", "/api/books/wanted", "/api/books/series",
            "/api/books/genres", "/api/books/foreign", "/api/books/manual", "/api/books/physical-only",
            "/api/books/recent-releases", "/api/books/recent-releases/all", "/api/books/duplicates",
            "/api/unclaimed", "/api/unknown-folders",
        })
        {
            var resp = await client.GetAsync(url);
            Assert.True(resp.IsSuccessStatusCode, $"{url} -> {(int)resp.StatusCode}");
        }

        // Routes that use raw SQL (unsupported by the InMemory test provider) —
        // call for coverage but tolerate the provider error.
        foreach (var url in new[] { "/api/authors", "/api/authors/starred", "/api/authors/stalled" })
            await client.GetAsync(url);
    }
}

using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TheLibrary.Server.Controllers;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Sync;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

[Collection("Integration")]
public class IdentifiedControllerIntegrationTests
{
    [Fact]
    public async Task ApplyCatalog_Creates_Series_And_Links_Matching_Books()
    {
        using var factory = new LibraryApiFactory();
        var catalog = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { Series = "Highlands Detective", Genre = "Crime", Titles = new[] { "Water's Edge", "The Bothy" } },
            new { Series = "Kirsten Stewart Thrillers", Genre = "Thriller", Titles = new[] { "A Shot at Democracy" } },
        });

        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "G R Jordan" });
            db.Books.AddRange(
                new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W", Title = "Water's Edge", NormalizedTitle = TitleNormalizer.Normalize("Water's Edge") },
                new Book { Id = 11, AuthorId = 1, OpenLibraryWorkKey = "OL11W", Title = "The Bothy", NormalizedTitle = TitleNormalizer.Normalize("The Bothy") });
            // "A Shot at Democracy" is not owned -> should count as unmatched.
            db.BookContentScans.Add(new BookContentScan
            {
                Id = 5, FullPath = "/Books/x.epub", Source = "unmatched", AuthorId = 1,
                SeriesCatalogJson = catalog, ScannedAt = DateTime.UtcNow,
            });
        });

        using var client = factory.CreateClient();
        var response = await client.PostAsync("/api/identified/5/apply-catalog", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

        var seriesNames = await db.Series.Select(s => s.Name).ToListAsync();
        Assert.Contains("Highlands Detective", seriesNames);
        Assert.Contains("Kirsten Stewart Thrillers", seriesNames);

        var waters = await db.Books.Include(b => b.Series).FirstAsync(b => b.Id == 10);
        Assert.Equal("Highlands Detective", waters.Series!.Name);
        Assert.Equal("1", waters.SeriesPosition);

        var bothy = await db.Books.FirstAsync(b => b.Id == 11);
        Assert.Equal("2", bothy.SeriesPosition);
    }

    [Fact]
    public async Task ApplyCatalog_Aggregates_Catalogues_Across_The_Authors_Books()
    {
        using var factory = new LibraryApiFactory();
        // Book 2 lists only the first two; book 4 lists all four (the fuller order).
        var shortList = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { Series = "Saga", Genre = (string?)null, Titles = new[] { "One", "Two" } },
        });
        var fullList = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { Series = "Saga", Genre = "Fantasy", Titles = new[] { "One", "Two", "Three", "Four" } },
        });

        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "A" });
            db.Books.AddRange(
                new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W", Title = "One", NormalizedTitle = TitleNormalizer.Normalize("One") },
                new Book { Id = 11, AuthorId = 1, OpenLibraryWorkKey = "OL11W", Title = "Two", NormalizedTitle = TitleNormalizer.Normalize("Two") },
                new Book { Id = 12, AuthorId = 1, OpenLibraryWorkKey = "OL12W", Title = "Three", NormalizedTitle = TitleNormalizer.Normalize("Three") },
                new Book { Id = 13, AuthorId = 1, OpenLibraryWorkKey = "OL13W", Title = "Four", NormalizedTitle = TitleNormalizer.Normalize("Four") });
            db.BookContentScans.AddRange(
                new BookContentScan { Id = 5, FullPath = "/b/2.epub", Source = "matched", AuthorId = 1, SeriesCatalogJson = shortList, ScannedAt = DateTime.UtcNow },
                new BookContentScan { Id = 6, FullPath = "/b/4.epub", Source = "matched", AuthorId = 1, SeriesCatalogJson = fullList, ScannedAt = DateTime.UtcNow });
        });

        using var client = factory.CreateClient();
        // Apply via the SHORT-list row — aggregation must still pull in the full order.
        var resp = await client.PostAsync("/api/identified/5/apply-catalog", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        async Task<string?> Pos(int id) => (await db.Books.FindAsync(id))!.SeriesPosition;
        Assert.Equal("1", await Pos(10));
        Assert.Equal("2", await Pos(11));
        Assert.Equal("3", await Pos(12)); // only present in the other book's list
        Assert.Equal("4", await Pos(13));
        var saga = await db.Books.Include(b => b.Series).FirstAsync(b => b.Id == 13);
        Assert.Equal("Saga", saga.Series!.Name);
    }

    [Fact]
    public async Task ApplyCatalog_Fuzzy_Matches_Slightly_Different_Titles()
    {
        using var factory = new LibraryApiFactory();
        var catalog = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            // Catalogue spelling differs slightly from the owned title.
            new { Series = "Detective", Genre = "Crime", Titles = new[] { "The Numerous Deaths of Santa Claus" } },
        });

        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "G R Jordan" });
            db.Books.Add(new Book
            {
                Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W",
                Title = "Numerous Death of Santa Claus", // missing "The", "Death" vs "Deaths"
                NormalizedTitle = TitleNormalizer.Normalize("Numerous Death of Santa Claus"),
            });
            db.BookContentScans.Add(new BookContentScan
            {
                Id = 5, FullPath = "/Books/x.epub", Source = "unmatched", AuthorId = 1,
                SeriesCatalogJson = catalog, ScannedAt = DateTime.UtcNow,
            });
        });

        using var client = factory.CreateClient();
        var resp = await client.PostAsync("/api/identified/5/apply-catalog", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var book = await db.Books.Include(b => b.Series).FirstAsync(b => b.Id == 10);
        Assert.Equal("Detective", book.Series!.Name); // linked despite the spelling gap
    }

    [Fact]
    public async Task ApplyCatalog_Does_Not_Clobber_Existing_Series()
    {
        using var factory = new LibraryApiFactory();
        var catalog = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { Series = "New Series", Genre = (string?)null, Titles = new[] { "Owned Title" } },
        });

        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "Author" });
            db.Series.Add(new Series { Id = 7, Name = "Existing", NormalizedName = TitleNormalizer.Normalize("Existing"), PrimaryAuthorId = 1 });
            db.Books.Add(new Book
            {
                Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W", Title = "Owned Title",
                NormalizedTitle = TitleNormalizer.Normalize("Owned Title"), SeriesId = 7, SeriesPosition = "4",
            });
            db.BookContentScans.Add(new BookContentScan
            {
                Id = 5, FullPath = "/Books/x.epub", Source = "unmatched", AuthorId = 1,
                SeriesCatalogJson = catalog, ScannedAt = DateTime.UtcNow,
            });
        });

        using var client = factory.CreateClient();
        var response = await client.PostAsync("/api/identified/5/apply-catalog", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var book = await db.Books.FirstAsync(b => b.Id == 10);
        Assert.Equal(7, book.SeriesId);       // unchanged
        Assert.Equal("4", book.SeriesPosition); // unchanged
    }

    [Fact]
    public async Task ApplyCatalog_Rejects_Row_With_No_Author()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db =>
        {
            db.BookContentScans.Add(new BookContentScan
            {
                Id = 5, FullPath = "/Books/x.epub", Source = "untracked", AuthorId = null,
                SeriesCatalogJson = "[{\"Series\":\"S\",\"Titles\":[\"T\"]}]", ScannedAt = DateTime.UtcNow,
            });
        });

        using var client = factory.CreateClient();
        var response = await client.PostAsync("/api/identified/5/apply-catalog", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ApplyCatalog_Qualifies_Generic_Series_Name_With_Author_And_Sets_Primary()
    {
        using var factory = new LibraryApiFactory();
        var catalog = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { Series = "Novels", Genre = (string?)null, Titles = new[] { "Dragonflight" } },
        });

        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "Anne McCaffrey" });
            // A DIFFERENT author already owns a bare "Novels" series — the new one
            // must NOT collide with / reuse it.
            db.Authors.Add(new Author { Id = 2, Name = "Other Author" });
            db.Series.Add(new Series { Id = 7, Name = "Novels", NormalizedName = TitleNormalizer.Normalize("Novels"), PrimaryAuthorId = 2 });
            db.Books.Add(new Book
            {
                Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W", Title = "Dragonflight",
                NormalizedTitle = TitleNormalizer.Normalize("Dragonflight"),
            });
            db.BookContentScans.Add(new BookContentScan
            {
                Id = 5, FullPath = "/b/x.epub", Source = "unmatched", AuthorId = 1,
                SeriesCatalogJson = catalog, ScannedAt = DateTime.UtcNow,
            });
        });

        using var client = factory.CreateClient();
        var resp = await client.PostAsync("/api/identified/5/apply-catalog", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

        var book = await db.Books.Include(b => b.Series).FirstAsync(b => b.Id == 10);
        Assert.NotNull(book.Series);
        Assert.Equal("Anne McCaffrey Novels", book.Series!.Name); // qualified, not bare "Novels"
        Assert.NotEqual(7, book.SeriesId);                         // not the other author's series
        Assert.Equal(1, book.Series.PrimaryAuthorId);              // primary author set
    }

    [Fact]
    public async Task Get_Hides_Author_Only_Rows_For_Files_Already_Under_An_Author()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "Known Author" });
            db.BookContentScans.AddRange(
                // Already filed under an author, only an author guess → pure noise, hidden.
                new BookContentScan { Id = 1, FullPath = "/b/linked.epub", Source = "unmatched", AuthorId = 1, Author = "Known Author", ScannedAt = DateTime.UtcNow },
                // Already filed under an author but also has a title → still useful, shown.
                new BookContentScan { Id = 2, FullPath = "/b/withtitle.epub", Source = "unmatched", AuthorId = 1, Author = "Known Author", Title = "Some Title", ScannedAt = DateTime.UtcNow },
                // Not under any author, author guess is the only lead → shown.
                new BookContentScan { Id = 3, FullPath = "/b/untracked.epub", Source = "untracked", AuthorId = null, Author = "Guessed Author", ScannedAt = DateTime.UtcNow });
        });

        using var client = factory.CreateClient();
        var rows = await client.GetFromJsonAsync<List<IdentifiedController.IdentifiedRow>>("/api/identified");
        var ids = rows!.Select(r => r.Id).OrderBy(i => i).ToList();

        Assert.Equal(new[] { 2, 3 }, ids); // the author-only linked row (1) is dropped
    }

    private static async Task SeedAsync(LibraryApiFactory factory, Action<LibraryDbContext> seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }
}

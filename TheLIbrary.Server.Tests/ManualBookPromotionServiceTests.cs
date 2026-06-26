using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Services.Scheduling;
using TheLibrary.Server.Services.Sync;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

public class ManualBookPromotionServiceTests
{
    [Fact]
    public async Task Promotes_Manual_Book_In_Place_Preserving_Series_And_Ownership()
    {
        var dbName = NewDb();
        await using (var db = CreateDb(dbName))
        {
            var author = new Author { Id = 1, Name = "Quigley Fenwick", OpenLibraryKey = "OL1A" };
            var series = new Series { Id = 5, Name = "Glimmer Saga", NormalizedName = "glimmer saga", PrimaryAuthorId = 1 };
            db.Authors.Add(author);
            db.Series.Add(series);
            db.Books.Add(new Book
            {
                Id = 10, AuthorId = 1, Title = "The Glimmer Quest",
                NormalizedTitle = TitleNormalizer.Normalize("The Glimmer Quest"),
                OpenLibraryWorkKey = "XX00000001W",
                SeriesId = 5, SeriesPosition = "2",
                ManuallyOwned = true, ManuallyOwnedAt = DateTime.UtcNow,
                Subjects = "",
            });
            await db.SaveChangesAsync();
        }

        var sut = CreateService(dbName, """
            {"numFound":1,"docs":[{"key":"/works/OL9W","title":"The Glimmer Quest",
              "first_publish_year":1999,"cover_i":42,
              "author_key":["OL1A"],"author_name":["Quigley Fenwick"]}]}
            """);
        var summary = await sut.RunForTestsAsync(CancellationToken.None);

        Assert.Equal(1, summary.Promoted);
        Assert.Equal(0, summary.Merged);
        await using var verify = CreateDb(dbName);
        var book = await verify.Books.SingleAsync();
        Assert.Equal("OL9W", book.OpenLibraryWorkKey);   // no longer manual
        Assert.Equal(5, book.SeriesId);                   // series kept
        Assert.Equal("2", book.SeriesPosition);
        Assert.True(book.ManuallyOwned);                  // ownership kept
        Assert.Equal(1999, book.FirstPublishYear);        // OL fields refreshed
        Assert.Equal(42, book.CoverId);
        Assert.Equal(new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc), book.CreatedAt); // past-year → re-dated, not "today"
    }

    [Fact]
    public async Task Merges_Manual_Book_Into_Existing_OL_Row_Moving_Series_And_Files()
    {
        var dbName = NewDb();
        await using (var db = CreateDb(dbName))
        {
            var author = new Author { Id = 1, Name = "Quigley Fenwick", OpenLibraryKey = "OL1A" };
            var series = new Series { Id = 5, Name = "Glimmer Saga", NormalizedName = "glimmer saga", PrimaryAuthorId = 1 };
            db.Authors.Add(author);
            db.Series.Add(series);
            // The OL row the refresh already created — no series, not owned.
            db.Books.Add(new Book
            {
                Id = 20, AuthorId = 1, Title = "The Glimmer Quest",
                NormalizedTitle = TitleNormalizer.Normalize("The Glimmer Quest"), OpenLibraryWorkKey = "OL9W", Subjects = "",
            });
            // The manual duplicate carrying the user's data and a linked file.
            db.Books.Add(new Book
            {
                Id = 10, AuthorId = 1, Title = "The Glimmer Quest",
                NormalizedTitle = TitleNormalizer.Normalize("The Glimmer Quest"), OpenLibraryWorkKey = "XX00000001W",
                SeriesId = 5, SeriesPosition = "2", ManuallyOwned = true, Subjects = "",
            });
            db.LocalBookFiles.Add(new LocalBookFile { Id = 1, BookId = 10, FullPath = "/lib/A/x.epub" });
            await db.SaveChangesAsync();
        }

        var sut = CreateService(dbName, """
            {"numFound":1,"docs":[{"key":"/works/OL9W","title":"The Glimmer Quest",
              "author_key":["OL1A"],"author_name":["Quigley Fenwick"]}]}
            """);
        var summary = await sut.RunForTestsAsync(CancellationToken.None);

        Assert.Equal(0, summary.Promoted);
        Assert.Equal(1, summary.Merged);
        await using var verify = CreateDb(dbName);
        var book = await verify.Books.SingleAsync();      // manual row deleted
        Assert.Equal(20, book.Id);
        Assert.Equal(5, book.SeriesId);                   // series moved across
        Assert.Equal("2", book.SeriesPosition);
        Assert.True(book.ManuallyOwned);                  // ownership moved across
        var file = await verify.LocalBookFiles.SingleAsync();
        Assert.Equal(20, file.BookId);                    // file follows the merge
    }

    [Fact]
    public async Task Merges_Manual_Duplicate_From_DB_Without_Any_OL_Search()
    {
        var dbName = NewDb();
        await using (var db = CreateDb(dbName))
        {
            db.Authors.Add(new Author { Id = 1, Name = "Quigley Fenwick", OpenLibraryKey = "OL1A" });
            // Real OL row already under the author (e.g. from the author refresh).
            db.Books.Add(new Book
            {
                Id = 20, AuthorId = 1, Title = "The Glimmer Quest",
                NormalizedTitle = TitleNormalizer.Normalize("The Glimmer Quest"), OpenLibraryWorkKey = "OL9W", Subjects = "",
            });
            // Manual placeholder duplicate the series builder minted.
            db.Books.Add(new Book
            {
                Id = 10, AuthorId = 1, Title = "The Glimmer Quest",
                NormalizedTitle = TitleNormalizer.Normalize("The Glimmer Quest"), OpenLibraryWorkKey = "XX00000001W",
                OwnedDifferentEdition = true, Subjects = "",
            });
            await db.SaveChangesAsync();
        }

        // OL returns NOTHING — the merge must still happen, purely from the DB.
        var sut = CreateService(dbName, """{"numFound":0,"docs":[]}""");
        var summary = await sut.RunForTestsAsync(CancellationToken.None);

        Assert.Equal(1, summary.Merged);
        Assert.Equal(0, summary.Promoted);
        Assert.Equal(0, summary.Remaining);
        await using var verify = CreateDb(dbName);
        var book = await verify.Books.SingleAsync();      // manual row gone
        Assert.Equal(20, book.Id);
        Assert.True(book.OwnedDifferentEdition);           // ownership signal carried over
    }

    [Fact]
    public async Task Does_Not_Promote_When_The_Search_Hit_Is_Another_Authors_Work()
    {
        var dbName = NewDb();
        await using (var db = CreateDb(dbName))
        {
            db.Authors.Add(new Author { Id = 1, Name = "Quigley Fenwick", OpenLibraryKey = "OL1A" });
            db.Books.Add(new Book
            {
                Id = 10, AuthorId = 1, Title = "The Glimmer Quest",
                NormalizedTitle = TitleNormalizer.Normalize("The Glimmer Quest"), OpenLibraryWorkKey = "XX00000001W", Subjects = "",
            });
            await db.SaveChangesAsync();
        }

        // Same title, different author — must be refused.
        var sut = CreateService(dbName, """
            {"numFound":1,"docs":[{"key":"/works/OL9W","title":"The Glimmer Quest",
              "author_key":["OL999A"],"author_name":["Somebody Else"]}]}
            """);
        var summary = await sut.RunForTestsAsync(CancellationToken.None);

        Assert.Equal(0, summary.Promoted + summary.Merged);
        Assert.Equal(1, summary.NotFound);
        await using var verify = CreateDb(dbName);
        Assert.Equal("XX00000001W", (await verify.Books.SingleAsync()).OpenLibraryWorkKey);
    }

    [Fact]
    public async Task Checks_Oldest_First_And_Respects_The_Per_Run_Limit()
    {
        var dbName = NewDb();
        var earlier = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await using (var db = CreateDb(dbName))
        {
            db.Authors.Add(new Author { Id = 1, Name = "Quigley Fenwick", OpenLibraryKey = "OL1A" });
            // Never checked (null) — must be picked first.
            db.Books.Add(new Book
            {
                Id = 10, AuthorId = 1, Title = "Aaa",
                NormalizedTitle = TitleNormalizer.Normalize("Aaa"), OpenLibraryWorkKey = "XX00000001W", Subjects = "",
            });
            // Already checked a while ago — must NOT be touched when the limit is 1.
            db.Books.Add(new Book
            {
                Id = 11, AuthorId = 1, Title = "Bbb",
                NormalizedTitle = TitleNormalizer.Normalize("Bbb"), OpenLibraryWorkKey = "XX00000002W",
                PromoteCheckedAt = earlier, Subjects = "",
            });
            db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.PromoteManualBooksMaxPerRun, Value = "1" });
            await db.SaveChangesAsync();
        }

        // OL returns nothing → not found, but the picked book is still stamped.
        var sut = CreateService(dbName, """{"numFound":0,"docs":[]}""");
        var summary = await sut.RunForTestsAsync(CancellationToken.None);

        Assert.Equal(1, summary.Examined); // only one book this run (limit = 1)
        await using var verify = CreateDb(dbName);
        var neverBefore = await verify.Books.FirstAsync(b => b.Id == 10);
        var checkedEarlier = await verify.Books.FirstAsync(b => b.Id == 11);
        Assert.NotNull(neverBefore.PromoteCheckedAt);          // null one was checked first
        Assert.Equal(earlier, checkedEarlier.PromoteCheckedAt); // older one was skipped this run
    }

    private static string NewDb() => $"manual-promotion-tests-{Guid.NewGuid():N}";

    private static LibraryDbContext CreateDb(string name)
        => new(new DbContextOptionsBuilder<LibraryDbContext>().UseInMemoryDatabase(name).Options);

    private static ManualBookPromotionService CreateService(string dbName, string searchJson)
    {
        var settings = new OpenLibrarySettings(new NoopScopeFactory());
        var limiter = new OpenLibraryRateLimiter(settings);
        var http = new HttpClient(new TestHttpMessageHandler((_, _) =>
            Task.FromResult(TestHttpMessageHandler.Json(searchJson))))
        {
            BaseAddress = new Uri("https://openlibrary.org/")
        };
        var ol = new OpenLibraryClient(http, limiter, settings, NullLogger<OpenLibraryClient>.Instance);

        var services = new ServiceCollection();
        services.AddDbContext<LibraryDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        services.AddSingleton(ol);
        var provider = services.BuildServiceProvider();

        return new ManualBookPromotionService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new BackgroundTaskCoordinator(),
            NullLogger<ManualBookPromotionService>.Instance);
    }
}

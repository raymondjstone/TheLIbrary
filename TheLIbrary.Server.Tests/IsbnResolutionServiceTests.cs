using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

public class IsbnResolutionServiceTests
{
    // OpenLibrary handler that always returns "no work" so the fallback chain runs.
    private static OpenLibraryClient MissingOl()
    {
        var http = new HttpClient(new TestHttpMessageHandler((_, _) =>
            Task.FromResult(TestHttpMessageHandler.Json("""{"numFound":0,"docs":[]}"""))));
        var settings = new OpenLibrarySettings(null!);
        return new OpenLibraryClient(http, new OpenLibraryRateLimiter(settings), settings, NullLogger<OpenLibraryClient>.Instance);
    }

    // OpenLibrary returns a work with a title but NO author (the "New Lensman" case).
    private static OpenLibraryClient AuthorlessOl()
    {
        var http = new HttpClient(new TestHttpMessageHandler((_, _) =>
            Task.FromResult(TestHttpMessageHandler.Json("""
                {"numFound":1,"docs":[{"key":"/works/OL44094142W","title":"New Lensman"}]}
                """))));
        var settings = new OpenLibrarySettings(null!);
        return new OpenLibraryClient(http, new OpenLibraryRateLimiter(settings), settings, NullLogger<OpenLibraryClient>.Instance);
    }

    private sealed class FakeProvider : IIsbnFallbackProvider
    {
        private readonly IsbnLookupResult _result;
        public FakeProvider(IsbnLookupResult result) => _result = result;
        public string Name => "Fake";
        public string CredentialSettingKey => "FakeKey";
        public Task<IsbnLookupResult> LookupAsync(string isbn, string? cred, CancellationToken ct) => Task.FromResult(_result);
    }

    private static IsbnResolutionService Service(RelationalTestDb rdb, params IIsbnFallbackProvider[] providers)
        => new(rdb.NewContext(), MissingOl(), providers);

    [Fact]
    public async Task Fallback_Hit_Is_Cached_With_Title_But_No_WorkKey()
    {
        using var rdb = new RelationalTestDb();
        var svc = Service(rdb, new FakeProvider(IsbnLookupResult.Found("Indie Book", "Jane Indie", 2021)));

        var row = await svc.ResolveAsync("978-0-9968450-0-7", default);

        Assert.NotNull(row);
        Assert.Equal("Indie Book", row!.Title);
        Assert.Equal("Jane Indie", row.AuthorName);
        Assert.Null(row.WorkKey);   // came from a fallback source, not OpenLibrary

        await using var v = rdb.NewContext();
        Assert.Equal("Indie Book", (await v.IsbnResolutions.FindAsync("9780996845007"))!.Title);
    }

    [Fact]
    public async Task First_Provider_To_Hit_Wins_Order_Respected()
    {
        using var rdb = new RelationalTestDb();
        var svc = Service(rdb,
            new FakeProvider(IsbnLookupResult.Miss),
            new FakeProvider(IsbnLookupResult.Found("Second Source Book", "Author Two", null)),
            new FakeProvider(IsbnLookupResult.Found("Third", "Nope", null)));

        var row = await svc.ResolveAsync("9780996845007", default);
        Assert.Equal("Second Source Book", row!.Title);
    }

    [Fact]
    public async Task All_Miss_Caches_A_Total_Miss()
    {
        using var rdb = new RelationalTestDb();
        var svc = Service(rdb, new FakeProvider(IsbnLookupResult.Miss), new FakeProvider(IsbnLookupResult.Skipped));

        var row = await svc.ResolveAsync("9780996845007", default);
        Assert.NotNull(row);
        Assert.Null(row!.Title);
        Assert.Null(row.WorkKey);   // remembered miss

        await using var v = rdb.NewContext();
        Assert.True(await v.IsbnResolutions.AnyAsync(r => r.Isbn == "9780996845007"));
    }

    [Fact]
    public async Task Unavailable_With_No_Hit_Throws_And_Caches_Nothing()
    {
        using var rdb = new RelationalTestDb();
        var svc = Service(rdb, new FakeProvider(IsbnLookupResult.Miss), new FakeProvider(IsbnLookupResult.Unavailable));

        await Assert.ThrowsAsync<IsbnLookupUnavailableException>(() => svc.ResolveAsync("9780996845007", default));

        await using var v = rdb.NewContext();
        Assert.False(await v.IsbnResolutions.AnyAsync(r => r.Isbn == "9780996845007"));  // not cached → retried later
    }

    [Fact]
    public async Task Ol_Work_Without_Author_Is_Enriched_From_A_Fallback()
    {
        using var rdb = new RelationalTestDb();
        // OL supplies the work key + title but no author; the fallback supplies the author.
        var svc = new IsbnResolutionService(rdb.NewContext(), AuthorlessOl(),
            new IIsbnFallbackProvider[] { new FakeProvider(IsbnLookupResult.Found("New Lensman", "William B. Ellern", 1976)) });

        var row = await svc.ResolveAsync("0860079236", default);

        Assert.NotNull(row);
        Assert.Equal("/works/OL44094142W", row!.WorkKey);   // OL work key kept
        Assert.Equal("New Lensman", row.Title);             // OL title kept
        Assert.Equal("William B. Ellern", row.AuthorName);  // author filled from the fallback
    }

    [Fact]
    public async Task Existing_Title_No_Author_Row_Is_Enriched_In_Place_When_Stale()
    {
        using var rdb = new RelationalTestDb();
        await using (var s = rdb.NewContext())
        {
            s.IsbnResolutions.Add(new IsbnResolution
            {
                Isbn = "9780996845007", Title = "New Lensman", WorkKey = "/works/OL44094142W",
                ResolvedAt = DateTime.UtcNow.AddDays(-1),   // stale → eligible for a second chance
            });
            await s.SaveChangesAsync();
        }
        var svc = new IsbnResolutionService(rdb.NewContext(), MissingOl(),
            new IIsbnFallbackProvider[] { new FakeProvider(IsbnLookupResult.Found("New Lensman", "William B. Ellern", 1976)) });

        var row = await svc.ResolveAsync("978-0-9968450-0-7", default);

        Assert.Equal("William B. Ellern", row!.AuthorName);   // filled from the fallback
        Assert.Equal("/works/OL44094142W", row.WorkKey);      // OL work key preserved

        await using var v = rdb.NewContext();
        Assert.Equal("William B. Ellern", (await v.IsbnResolutions.FindAsync("9780996845007"))!.AuthorName); // persisted
    }

    [Fact]
    public async Task Existing_Title_No_Author_Row_Is_Not_Re_Attempted_When_Recent()
    {
        using var rdb = new RelationalTestDb();
        await using (var s = rdb.NewContext())
        {
            s.IsbnResolutions.Add(new IsbnResolution
            {
                Isbn = "9780996845007", Title = "New Lensman",
                ResolvedAt = DateTime.UtcNow,   // just resolved → don't burn quota re-attempting
            });
            await s.SaveChangesAsync();
        }
        var svc = new IsbnResolutionService(rdb.NewContext(), MissingOl(),
            new IIsbnFallbackProvider[] { new FakeProvider(IsbnLookupResult.Found("New Lensman", "Would-Be Author", null)) });

        var row = await svc.ResolveAsync("9780996845007", default);

        Assert.Null(row!.AuthorName);   // not re-attempted this soon
    }

    [Fact]
    public async Task Ol_Work_Without_Author_Is_Cached_As_Is_When_No_Fallback_Has_The_Author()
    {
        using var rdb = new RelationalTestDb();
        var svc = new IsbnResolutionService(rdb.NewContext(), AuthorlessOl(),
            new IIsbnFallbackProvider[] { new FakeProvider(IsbnLookupResult.Miss) });

        var row = await svc.ResolveAsync("0860079236", default);

        Assert.Equal("New Lensman", row!.Title);   // still useful — title shown, author unknown
        Assert.Null(row.AuthorName);
    }

    [Fact]
    public async Task Unavailable_But_A_Later_Provider_Hits_Is_Cached()
    {
        using var rdb = new RelationalTestDb();
        var svc = Service(rdb,
            new FakeProvider(IsbnLookupResult.Unavailable),
            new FakeProvider(IsbnLookupResult.Found("Rescued By ISBNdb", "Indie Author", 2020)));

        var row = await svc.ResolveAsync("9780996845007", default);
        Assert.Equal("Rescued By ISBNdb", row!.Title);   // a hit trumps an earlier Unavailable
    }
}

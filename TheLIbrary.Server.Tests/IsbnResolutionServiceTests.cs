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

    // OpenLibrary returns a doc with a key and an author but NO title at all — an
    // edge case distinct from "no doc": the fallback chain must still run to get a
    // usable title, since a doc merely existing isn't the same as having one.
    private static OpenLibraryClient TitlelessOl()
    {
        var http = new HttpClient(new TestHttpMessageHandler((_, _) =>
            Task.FromResult(TestHttpMessageHandler.Json("""
                {"numFound":1,"docs":[{"key":"/works/OL1W","author_name":["Some Author"]}]}
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

    // When OpenLibrary has NOTHING, a title alone is enough — the file keeps its
    // existing/folder author regardless of what a fallback source says, so once a
    // source supplies a title (even with no author) the chain must stop rather than
    // keep spending calls on later sources purely to chase an author that will never
    // be used. The second provider throwing proves it's never reached.
    [Fact]
    public async Task A_Title_Only_Hit_Stops_The_Chain_When_OL_Had_Nothing()
    {
        using var rdb = new RelationalTestDb();
        var svc = Service(rdb,
            new FakeProvider(IsbnLookupResult.Found("Title Only From ISBNdb", null, null)),
            new ThrowingProvider());

        var row = await svc.ResolveAsync("9780996845007", default);

        Assert.NotNull(row);
        Assert.Equal("Title Only From ISBNdb", row!.Title);
        Assert.Null(row.AuthorName);
    }

    private sealed class ThrowingProvider : IIsbnFallbackProvider
    {
        public string Name => "Fake";
        public string CredentialSettingKey => "FakeKey";
        public Task<IsbnLookupResult> LookupAsync(string isbn, string? cred, CancellationToken ct)
            => throw new InvalidOperationException("Should not be called once a title was found.");
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
    public async Task All_Unavailable_With_No_Definitive_Answer_Throws_And_Caches_Nothing()
    {
        using var rdb = new RelationalTestDb();
        var svc = Service(rdb, new FakeProvider(IsbnLookupResult.Unavailable), new FakeProvider(IsbnLookupResult.Unavailable));

        await Assert.ThrowsAsync<IsbnLookupUnavailableException>(() => svc.ResolveAsync("9780996845007", default));

        await using var v = rdb.NewContext();
        Assert.False(await v.IsbnResolutions.AnyAsync(r => r.Isbn == "9780996845007"));  // not cached → retried later
    }

    // A source being rate/quota-capped (e.g. Google's daily quota spent) must not block
    // caching a miss that ANOTHER source already definitively checked — otherwise every
    // self-published ISBN still in the backlog gets deferred and re-tried for nothing
    // once that one source's quota is blown for the day, and the catch-up job appears to
    // make almost no progress even though Hardcover/LoC already said nobody has it.
    [Fact]
    public async Task Unavailable_Source_Does_Not_Block_A_Definitive_Miss_From_Another()
    {
        using var rdb = new RelationalTestDb();
        var svc = Service(rdb, new FakeProvider(IsbnLookupResult.Unavailable), new FakeProvider(IsbnLookupResult.Miss));

        var row = await svc.ResolveAsync("9780996845007", default);
        Assert.NotNull(row);
        Assert.Null(row!.Title);   // remembered as a total miss, not deferred

        await using var v = rdb.NewContext();
        Assert.True(await v.IsbnResolutions.AnyAsync(r => r.Isbn == "9780996845007"));
    }

    // Without a fail-attempt limit, an ISBN that every source is (temporarily or
    // permanently) unavailable for would be re-attempted forever. Below the configured
    // limit it must keep throwing (retry later); once it reaches the limit it should
    // give up and cache a permanent miss instead — same shape as any other unresolvable
    // ISBN — so the catch-up job stops re-picking it run after run.
    [Fact]
    public async Task Repeated_Total_Failure_Gives_Up_After_Configured_Limit()
    {
        using var rdb = new RelationalTestDb();
        await using (var seed = rdb.NewContext())
        {
            seed.AppSettings.Add(new AppSetting { Key = AppSettingKeys.IsbnResolveMaxFailedAttempts, Value = "2" });
            await seed.SaveChangesAsync();
        }
        var svc = Service(rdb, new FakeProvider(IsbnLookupResult.Unavailable));

        // 1st attempt: below the limit — still thrown, nothing cached.
        await Assert.ThrowsAsync<IsbnLookupUnavailableException>(() => svc.ResolveAsync("9780996845007", default));
        await using (var v1 = rdb.NewContext())
        {
            Assert.False(await v1.IsbnResolutions.AnyAsync(r => r.Isbn == "9780996845007"));
            Assert.Equal(1, (await v1.IsbnResolutionAttempts.FindAsync("9780996845007"))!.FailCount);
        }

        // 2nd attempt: hits the limit — gives up, caches a permanent miss, and clears
        // the attempt-tracking row.
        var row = await svc.ResolveAsync("9780996845007", default);
        Assert.NotNull(row);
        Assert.Null(row!.Title);

        await using var v2 = rdb.NewContext();
        Assert.True(await v2.IsbnResolutions.AnyAsync(r => r.Isbn == "9780996845007"));
        Assert.Null(await v2.IsbnResolutionAttempts.FindAsync("9780996845007"));
    }

    // A source being unavailable must not, on its own, tip the fail counter closer to
    // the give-up limit once a DIFFERENT source has already produced a real answer —
    // the counter only tracks total-failure attempts, not every call.
    [Fact]
    public async Task A_Later_Success_Clears_A_Previously_Recorded_Failure()
    {
        using var rdb = new RelationalTestDb();
        await using (var seed = rdb.NewContext())
        {
            seed.AppSettings.Add(new AppSetting { Key = AppSettingKeys.IsbnResolveMaxFailedAttempts, Value = "5" });
            await seed.SaveChangesAsync();
        }

        // First: every source unavailable — records one failure, throws.
        var svc1 = Service(rdb, new FakeProvider(IsbnLookupResult.Unavailable));
        await Assert.ThrowsAsync<IsbnLookupUnavailableException>(() => svc1.ResolveAsync("9780996845007", default));
        await using (var v1 = rdb.NewContext())
            Assert.Equal(1, (await v1.IsbnResolutionAttempts.FindAsync("9780996845007"))!.FailCount);

        // Then: a source comes through with a real hit — the stale failure row is dropped.
        var svc2 = Service(rdb, new FakeProvider(IsbnLookupResult.Found("Indie Book", "Jane Indie", 2021)));
        var row = await svc2.ResolveAsync("9780996845007", default);
        Assert.Equal("Indie Book", row!.Title);

        await using var v2 = rdb.NewContext();
        Assert.Null(await v2.IsbnResolutionAttempts.FindAsync("9780996845007"));
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

    // OpenLibrary can return a doc (a Key, even an author) with NO title at all. The
    // fallback chain must still run in this case — a doc merely existing is not the
    // same as having a usable title — so ISBNdb (or whichever source) still gets a
    // chance to supply one instead of the row being cached with a blank title forever.
    [Fact]
    public async Task Fallback_Chain_Runs_When_OL_Doc_Has_No_Title()
    {
        using var rdb = new RelationalTestDb();
        var svc = new IsbnResolutionService(rdb.NewContext(), TitlelessOl(),
            new IIsbnFallbackProvider[] { new FakeProvider(IsbnLookupResult.Found("Rescued Title", null, null)) });

        var row = await svc.ResolveAsync("9780996845007", default);

        Assert.NotNull(row);
        Assert.Equal("/works/OL1W", row!.WorkKey);       // OL's work key kept
        Assert.Equal("Rescued Title", row.Title);        // title filled from the fallback
    }
}

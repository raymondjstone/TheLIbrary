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

public class IsbnResolutionCatchupServiceTests
{
    // Returns Unavailable for one target ISBN, Hit for everything else.
    private sealed class OneUnavailableProvider : IIsbnFallbackProvider
    {
        private readonly string _unavailableIsbn;
        public OneUnavailableProvider(string unavailableIsbn) => _unavailableIsbn = unavailableIsbn;
        public string Name => "Fake";
        public string CredentialSettingKey => "FakeKey";
        public Task<IsbnLookupResult> LookupAsync(string isbn, string? cred, CancellationToken ct)
            => Task.FromResult(isbn == _unavailableIsbn
                ? IsbnLookupResult.Unavailable
                : IsbnLookupResult.Found("Resolved Title", "Resolved Author", 2020));
    }

    private static OpenLibraryClient MissingOl()
    {
        var http = new HttpClient(new TestHttpMessageHandler((_, _) =>
            Task.FromResult(TestHttpMessageHandler.Json("""{"numFound":0,"docs":[]}"""))));
        var settings = new OpenLibrarySettings(null!);
        return new OpenLibraryClient(http, new OpenLibraryRateLimiter(settings), settings, NullLogger<OpenLibraryClient>.Instance);
    }

    // Resolves every ISBN as a hit — used by the ordering tests, where the point is
    // WHICH ISBNs get attempted this run, not how a miss/unavailable is handled.
    private sealed class AlwaysHitProvider : IIsbnFallbackProvider
    {
        public string Name => "Fake";
        public string CredentialSettingKey => "FakeKey";
        public Task<IsbnLookupResult> LookupAsync(string isbn, string? cred, CancellationToken ct)
            => Task.FromResult(IsbnLookupResult.Found("Resolved Title", "Resolved Author", 2020));
    }

    [Fact]
    public async Task Continues_Past_An_Unavailable_Isbn_Instead_Of_Stopping()
    {
        // Three ISBNs; the MIDDLE one's source is unavailable. The job must still
        // resolve the other two (the regression was that it broke on the first
        // unavailable and did "only a few books").
        const string a = "9780307762726", bad = "9780648491798", c = "9781475960235";
        var dbName = $"isbn-catchup-{Guid.NewGuid():N}";

        var services = new ServiceCollection();
        services.AddDbContext<LibraryDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        services.AddSingleton(MissingOl());
        services.AddSingleton<IIsbnFallbackProvider>(new OneUnavailableProvider(bad));
        services.AddScoped<IsbnResolutionService>();
        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            var now = DateTime.UtcNow;
            db.BookContentScans.AddRange(
                new BookContentScan { Id = 1, FullPath = "/a", Source = "unmatched", Isbn = a, ScannedAt = now },
                new BookContentScan { Id = 2, FullPath = "/b", Source = "unmatched", Isbn = bad, ScannedAt = now },
                new BookContentScan { Id = 3, FullPath = "/c", Source = "unmatched", Isbn = c, ScannedAt = now });
            await db.SaveChangesAsync();
        }

        var sut = new IsbnResolutionCatchupService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new BackgroundTaskCoordinator(),
            NullLogger<IsbnResolutionCatchupService>.Instance);

        var summary = await sut.RunForTestsAsync(CancellationToken.None);

        Assert.Equal(2, summary.Found);              // both resolvable ISBNs done
        Assert.True(summary.Remaining >= 1);         // the unavailable one is still pending

        using var verify = provider.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LibraryDbContext>();
        Assert.NotNull(await vdb.IsbnResolutions.FindAsync(a));   // resolved + cached
        Assert.NotNull(await vdb.IsbnResolutions.FindAsync(c));   // resolved despite the earlier blip
        Assert.Null(await vdb.IsbnResolutions.FindAsync(bad));    // deferred, not cached → retried later
    }

    [Fact]
    public async Task A_Deferred_Isbn_Is_Not_Reattempted_The_Same_Day()
    {
        // One ISBN whose source is quota-capped: the first run defers it, and a second
        // run the same day must NOT spend a batch slot re-attempting it (the regression
        // was every run re-grinding the same stuck codes and caching nothing).
        const string bad = "9780648491798";
        var dbName = $"isbn-catchup-{Guid.NewGuid():N}";

        var services = new ServiceCollection();
        services.AddDbContext<LibraryDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        services.AddSingleton(MissingOl());
        services.AddSingleton<IIsbnFallbackProvider>(new OneUnavailableProvider(bad));
        services.AddScoped<IsbnResolutionService>();
        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            db.BookContentScans.Add(
                new BookContentScan { Id = 1, FullPath = "/b", Source = "unmatched", Isbn = bad, ScannedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var sut = new IsbnResolutionCatchupService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new BackgroundTaskCoordinator(),
            NullLogger<IsbnResolutionCatchupService>.Instance);

        var first = await sut.RunForTestsAsync(CancellationToken.None);
        Assert.Equal(1, first.Considered);           // attempted once
        Assert.Equal(0, first.Found);                // ...but deferred, not cached

        var second = await sut.RunForTestsAsync(CancellationToken.None);
        Assert.Equal(0, second.Considered);          // skipped — deferred earlier today
        Assert.True(second.Remaining >= 1);          // still reported as pending

        using var verify = provider.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LibraryDbContext>();
        Assert.Null(await vdb.IsbnResolutions.FindAsync(bad));    // still uncached → retried tomorrow
    }

    [Fact]
    public async Task Never_Attempted_Isbns_Are_Preferred_Over_Previously_Failed()
    {
        // Two fresh ISBNs (no attempt history) and one that already failed once. The
        // batch is capped to 2, so it must spend both slots on the fresh codes and
        // leave the previously-failed one for a later run.
        const string freshA = "9780307762726", freshB = "9781475960235", failedC = "9780648491798";
        var dbName = $"isbn-catchup-{Guid.NewGuid():N}";

        var services = new ServiceCollection();
        services.AddDbContext<LibraryDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        services.AddSingleton(MissingOl());
        services.AddSingleton<IIsbnFallbackProvider>(new AlwaysHitProvider());
        services.AddScoped<IsbnResolutionService>();
        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            var now = DateTime.UtcNow;
            db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.ResolveIsbnsMaxPerRun, Value = "2" });
            db.BookContentScans.AddRange(
                new BookContentScan { Id = 1, FullPath = "/a", Source = "unmatched", Isbn = freshA, ScannedAt = now },
                new BookContentScan { Id = 2, FullPath = "/b", Source = "unmatched", Isbn = freshB, ScannedAt = now },
                new BookContentScan { Id = 3, FullPath = "/c", Source = "unmatched", Isbn = failedC, ScannedAt = now });
            db.IsbnResolutionAttempts.Add(new IsbnResolutionAttempt { Isbn = failedC, FailCount = 1, LastAttemptAt = now });
            await db.SaveChangesAsync();
        }

        var sut = new IsbnResolutionCatchupService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new BackgroundTaskCoordinator(),
            NullLogger<IsbnResolutionCatchupService>.Instance);

        var summary = await sut.RunForTestsAsync(CancellationToken.None);
        Assert.Equal(2, summary.Considered);

        using var verify = provider.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LibraryDbContext>();
        Assert.NotNull(await vdb.IsbnResolutions.FindAsync(freshA));
        Assert.NotNull(await vdb.IsbnResolutions.FindAsync(freshB));
        Assert.Null(await vdb.IsbnResolutions.FindAsync(failedC));   // slots went to fresh codes instead
    }

    [Fact]
    public async Task Retries_Prefer_The_Lowest_Fail_Count_First()
    {
        // Both ISBNs have already failed before — none are "fresh" — so the tiebreak
        // is fail count: the one that's failed fewer times should be retried first,
        // leaving the more-often-failed one further from its give-up limit for later.
        const string lowFails = "9780307762726", highFails = "9781475960235";
        var dbName = $"isbn-catchup-{Guid.NewGuid():N}";

        var services = new ServiceCollection();
        services.AddDbContext<LibraryDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        services.AddSingleton(MissingOl());
        services.AddSingleton<IIsbnFallbackProvider>(new AlwaysHitProvider());
        services.AddScoped<IsbnResolutionService>();
        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            var now = DateTime.UtcNow;
            db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.ResolveIsbnsMaxPerRun, Value = "1" });
            db.BookContentScans.AddRange(
                new BookContentScan { Id = 1, FullPath = "/a", Source = "unmatched", Isbn = lowFails, ScannedAt = now },
                new BookContentScan { Id = 2, FullPath = "/b", Source = "unmatched", Isbn = highFails, ScannedAt = now });
            db.IsbnResolutionAttempts.AddRange(
                new IsbnResolutionAttempt { Isbn = lowFails, FailCount = 1, LastAttemptAt = now },
                new IsbnResolutionAttempt { Isbn = highFails, FailCount = 5, LastAttemptAt = now });
            await db.SaveChangesAsync();
        }

        var sut = new IsbnResolutionCatchupService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new BackgroundTaskCoordinator(),
            NullLogger<IsbnResolutionCatchupService>.Instance);

        var summary = await sut.RunForTestsAsync(CancellationToken.None);
        Assert.Equal(1, summary.Considered);

        using var verify = provider.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LibraryDbContext>();
        Assert.NotNull(await vdb.IsbnResolutions.FindAsync(lowFails));    // retried first
        Assert.Null(await vdb.IsbnResolutions.FindAsync(highFails));     // left for later
    }
}

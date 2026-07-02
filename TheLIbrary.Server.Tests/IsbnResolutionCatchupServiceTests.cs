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
}

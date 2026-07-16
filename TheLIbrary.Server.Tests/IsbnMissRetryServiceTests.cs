using Microsoft.Data.Sqlite;
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

// Uses a real SQLite connection (like RelationalTestDb), not the EF InMemory
// provider — the service's delete-then-resolve step uses ExecuteDeleteAsync, which
// InMemory can't execute (see RelationalTestDb.cs's own comment, and
// SettingsControllerIntegrationTests.ResetIsbnMisses_* for the same constraint).
public class IsbnMissRetryServiceTests
{
    private static OpenLibraryClient MissingOl()
    {
        var http = new HttpClient(new TestHttpMessageHandler((_, _) =>
            Task.FromResult(TestHttpMessageHandler.Json("""{"numFound":0,"docs":[]}"""))));
        var settings = new OpenLibrarySettings(null!);
        return new OpenLibraryClient(http, new OpenLibraryRateLimiter(settings), settings, NullLogger<OpenLibraryClient>.Instance);
    }

    // Resolves every ISBN as a hit — used where the point is which/how-many rows get
    // reprocessed, not miss/unavailable handling.
    private sealed class AlwaysHitProvider : IIsbnFallbackProvider
    {
        public string Name => "Fake";
        public string CredentialSettingKey => AppSettingKeys.GoogleBooksApiKey;
        public Task<IsbnLookupResult> LookupAsync(string isbn, string? cred, CancellationToken ct)
            => Task.FromResult(IsbnLookupResult.Found($"Title {isbn}", "Some Author", 2020));
    }

    // Never finds anything, and reports Unavailable so a re-resolve throws
    // IsbnLookupUnavailableException (a source was capped) rather than caching a miss.
    private sealed class AlwaysUnavailableProvider : IIsbnFallbackProvider
    {
        public string Name => "Fake";
        public string CredentialSettingKey => AppSettingKeys.GoogleBooksApiKey;
        public Task<IsbnLookupResult> LookupAsync(string isbn, string? cred, CancellationToken ct)
            => Task.FromResult(IsbnLookupResult.Unavailable);
    }

    // Simulates "Google's quota just got spent on this call": marks the shared
    // GoogleBooksRateLimiter exhausted as a side effect, then reports Unavailable
    // (as GoogleBooksFallbackProvider does after catching the quota exception).
    private sealed class QuotaBurningProvider : IIsbnFallbackProvider
    {
        private readonly GoogleBooksRateLimiter _limiter;
        public QuotaBurningProvider(GoogleBooksRateLimiter limiter) => _limiter = limiter;
        public string Name => "Fake";
        public string CredentialSettingKey => AppSettingKeys.GoogleBooksApiKey;
        public Task<IsbnLookupResult> LookupAsync(string isbn, string? cred, CancellationToken ct)
        {
            _limiter.MarkExhausted();
            return Task.FromResult(IsbnLookupResult.Unavailable);
        }
    }

    private static (ServiceProvider Provider, SqliteConnection Connection) BuildProvider(
        GoogleBooksRateLimiter limiter, params IIsbnFallbackProvider[] fallbackProviders)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<LibraryDbContext>(opt => opt.UseSqlite(connection));
        services.AddSingleton(MissingOl());
        foreach (var p in fallbackProviders)
            services.AddSingleton<IIsbnFallbackProvider>(p);
        services.AddScoped<IsbnResolutionService>();
        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            db.Database.EnsureCreated();
            db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.GoogleBooksApiKey, Value = "test-key" });
            db.SaveChanges();
        }

        return (provider, connection);
    }

    [Fact]
    public async Task Skips_Entirely_When_Google_Books_Is_Not_Configured()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var services = new ServiceCollection();
        services.AddDbContext<LibraryDbContext>(opt => opt.UseSqlite(connection));
        services.AddSingleton(MissingOl());
        services.AddScoped<IsbnResolutionService>();
        var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            db.Database.EnsureCreated();
            db.IsbnResolutions.Add(new IsbnResolution { Isbn = "9780307762726", ResolvedAt = DateTime.UtcNow });
            db.SaveChanges();
            // Deliberately no GoogleBooksApiKey setting.
        }

        var sut = new IsbnMissRetryService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new BackgroundTaskCoordinator(),
            new GoogleBooksRateLimiter(),
            NullLogger<IsbnMissRetryService>.Instance);

        var summary = await sut.RunForTestsAsync(CancellationToken.None);

        Assert.Equal(0, summary.Considered);
        Assert.Equal(1, summary.Remaining); // untouched — the row is still there, still null

        using var verify = provider.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LibraryDbContext>();
        Assert.NotNull(await vdb.IsbnResolutions.FindAsync("9780307762726"));
        connection.Dispose();
    }

    [Fact]
    public async Task Skips_Entirely_When_Googles_Quota_Is_Already_Exhausted_Today()
    {
        var limiter = new GoogleBooksRateLimiter();
        limiter.MarkExhausted();
        var (provider, connection) = BuildProvider(limiter, new AlwaysHitProvider());
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            db.IsbnResolutions.Add(new IsbnResolution { Isbn = "9780307762726", ResolvedAt = DateTime.UtcNow });
            db.SaveChanges();
        }

        var sut = new IsbnMissRetryService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new BackgroundTaskCoordinator(),
            limiter,
            NullLogger<IsbnMissRetryService>.Instance);

        var summary = await sut.RunForTestsAsync(CancellationToken.None);

        Assert.Equal(0, summary.Considered);
        connection.Dispose();
    }

    [Fact]
    public async Task Reprocesses_Oldest_Null_Title_Rows_First_And_Resolves_Them()
    {
        var limiter = new GoogleBooksRateLimiter();
        var (provider, connection) = BuildProvider(limiter, new AlwaysHitProvider());
        var now = DateTime.UtcNow;
        const string oldest = "9780307762726", middle = "9780648491798", newest = "9781475960235";
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            db.IsbnResolutions.AddRange(
                new IsbnResolution { Isbn = newest, ResolvedAt = now },
                new IsbnResolution { Isbn = oldest, ResolvedAt = now.AddDays(-30) },
                new IsbnResolution { Isbn = middle, ResolvedAt = now.AddDays(-10) });
            db.SaveChanges();
        }

        var sut = new IsbnMissRetryService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new BackgroundTaskCoordinator(),
            limiter,
            NullLogger<IsbnMissRetryService>.Instance);

        var summary = await sut.RunForTestsAsync(CancellationToken.None);

        Assert.Equal(3, summary.Considered);
        Assert.Equal(3, summary.Resolved);
        Assert.Equal(0, summary.Remaining);

        using var verify = provider.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var row = await vdb.IsbnResolutions.FindAsync(oldest);
        Assert.NotNull(row);
        Assert.Equal($"Title {oldest}", row!.Title); // the stale row was replaced, not left alone
        connection.Dispose();
    }

    [Fact]
    public async Task Stops_The_Run_The_Moment_Googles_Quota_Latches_Exhausted_Mid_Run()
    {
        var limiter = new GoogleBooksRateLimiter();
        var (provider, connection) = BuildProvider(limiter, new QuotaBurningProvider(limiter));
        var now = DateTime.UtcNow;
        const string a = "9780307762726", b = "9780648491798", c = "9781475960235";
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            db.IsbnResolutions.AddRange(
                new IsbnResolution { Isbn = a, ResolvedAt = now.AddDays(-3) },
                new IsbnResolution { Isbn = b, ResolvedAt = now.AddDays(-2) },
                new IsbnResolution { Isbn = c, ResolvedAt = now.AddDays(-1) });
            db.SaveChanges();
        }

        var sut = new IsbnMissRetryService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new BackgroundTaskCoordinator(),
            limiter,
            NullLogger<IsbnMissRetryService>.Instance);

        var summary = await sut.RunForTestsAsync(CancellationToken.None);

        // The very first row's lookup burns the quota, so the loop must stop before
        // ever reaching b or c — it must not grind through all three regardless.
        Assert.True(summary.Considered < 3, $"expected the run to stop early, but considered {summary.Considered}");
        Assert.True(limiter.IsExhaustedToday);
        connection.Dispose();
    }

    [Fact]
    public async Task A_Non_Google_Unavailable_Source_Defers_That_Isbn_But_The_Batch_Continues()
    {
        var limiter = new GoogleBooksRateLimiter();
        var (provider, connection) = BuildProvider(limiter, new AlwaysUnavailableProvider());
        var now = DateTime.UtcNow;
        const string a = "9780307762726", b = "9780648491798";
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            db.IsbnResolutions.AddRange(
                new IsbnResolution { Isbn = a, ResolvedAt = now.AddDays(-2) },
                new IsbnResolution { Isbn = b, ResolvedAt = now.AddDays(-1) });
            db.SaveChanges();
        }

        var sut = new IsbnMissRetryService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new BackgroundTaskCoordinator(),
            limiter,
            NullLogger<IsbnMissRetryService>.Instance);

        var summary = await sut.RunForTestsAsync(CancellationToken.None);

        // Neither resolved (the only source is unavailable), but BOTH must have been
        // attempted — a single unavailable source must not abort the whole batch.
        Assert.Equal(2, summary.Considered);
        Assert.Equal(0, summary.Resolved);
        connection.Dispose();
    }
}

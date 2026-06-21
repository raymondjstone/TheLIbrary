using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Scheduling;
using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class SeriesWatchServiceTests
{
    [Fact]
    public async Task Marks_New_Volume_In_Owned_Series_As_Wanted()
    {
        var dbName = "serieswatch-" + Guid.NewGuid().ToString("N");
        await using (var seed = NewDb(dbName))
        {
            seed.Authors.Add(new Author { Id = 1, Name = "Auth" });
            seed.Series.Add(new Series { Id = 1, Name = "Saga", NormalizedName = "saga", PrimaryAuthorId = 1 });
            // Owned volume in the series (has a file).
            seed.Books.Add(new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W", Title = "Saga 1", NormalizedTitle = "saga 1", SeriesId = 1, ManuallyOwned = true });
            // New, unowned volume added recently in the same series.
            seed.Books.Add(new Book { Id = 11, AuthorId = 1, OpenLibraryWorkKey = "OL11W", Title = "Saga 2", NormalizedTitle = "saga 2", SeriesId = 1, CreatedAt = DateTime.UtcNow });
            // A new volume in a series the user does NOT own — must be left alone.
            seed.Series.Add(new Series { Id = 2, Name = "Other", NormalizedName = "other", PrimaryAuthorId = 1 });
            seed.Books.Add(new Book { Id = 12, AuthorId = 1, OpenLibraryWorkKey = "OL12W", Title = "Other 1", NormalizedTitle = "other 1", SeriesId = 2, CreatedAt = DateTime.UtcNow });
            await seed.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddDbContext<LibraryDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();
        var sut = new SeriesWatchService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new BackgroundTaskCoordinator(), NullLogger<SeriesWatchService>.Instance);

        var result = await sut.RunForTestsAsync(CancellationToken.None);

        Assert.Equal(1, result.NewVolumes);
        await using var verify = NewDb(dbName);
        Assert.True((await verify.Books.FindAsync(11))!.Wanted);   // owned-series new volume → wanted
        Assert.False((await verify.Books.FindAsync(12))!.Wanted);  // unowned series → untouched
    }

    private static LibraryDbContext NewDb(string name)
        => new(new DbContextOptionsBuilder<LibraryDbContext>().UseInMemoryDatabase(name).Options);
}

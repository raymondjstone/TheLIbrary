using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Download;
using TheLibrary.Server.Services.Scheduling;
using Xunit;

namespace TheLibrary.Server.Tests;

public class AutoReplaceDamagedServiceTests
{
    // With download automation not configured, the job must skip cleanly (no grab
    // attempts) rather than throw — exercises the config guard + candidate query.
    [Fact]
    public async Task Skips_When_Download_Automation_Not_Configured()
    {
        var dbName = "autoreplace-" + Guid.NewGuid().ToString("N");
        await using (var seed = new LibraryDbContext(new DbContextOptionsBuilder<LibraryDbContext>().UseInMemoryDatabase(dbName).Options))
        {
            seed.Authors.Add(new Author { Id = 1, Name = "Auth" });
            seed.Books.Add(new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W", Title = "Book", NormalizedTitle = "book" });
            seed.LocalBookFiles.Add(new LocalBookFile { Id = 1, BookId = 10, AuthorId = 1, AuthorFolder = "Auth", FullPath = "/lib/Auth/book.epub", IntegrityOk = false, ModifiedAt = DateTime.UtcNow });
            await seed.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddDbContext<LibraryDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        services.AddHttpClient();
        services.AddScoped<NzbGrabService>();
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var sut = new AutoReplaceDamagedService(scopeFactory, new BackgroundTaskCoordinator(),
            NullLogger<AutoReplaceDamagedService>.Instance);

        var result = await sut.RunForTestsAsync(CancellationToken.None);

        Assert.False(result.Configured);
        Assert.Equal(0, result.Attempted);
    }
}

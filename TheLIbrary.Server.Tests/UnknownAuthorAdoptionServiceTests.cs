using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Scheduling;
using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class UnknownAuthorAdoptionServiceTests
{
    // An _OLkey author folder sitting in a LIBRARY root (not __unknown) with an
    // unclaimed file should be adopted IN PLACE: the author is created from the
    // key and the file is linked, with CalibreFolderName matching the on-disk
    // folder so the link survives sync. No Incoming folder configured.
    [Fact]
    public async Task Adopts_OLkey_Library_Folder_In_Place()
    {
        var dbName = "adopt-" + Guid.NewGuid().ToString("N");
        await using (var seed = NewDb(dbName))
        {
            seed.LocalBookFiles.Add(new LocalBookFile
            {
                Id = 1, AuthorId = null, BookId = null,
                AuthorFolder = "Francois Mauriac _OL15295370A",
                FullPath = "/Books/Francois Mauriac _OL15295370A/Therese - Francois Mauriac.epub",
                ModifiedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddDbContext<LibraryDbContext>(o => o.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();
        var sut = new UnknownAuthorAdoptionService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new BackgroundTaskCoordinator(), NullLogger<UnknownAuthorAdoptionService>.Instance);

        var result = await sut.RunForTestsAsync(CancellationToken.None);

        Assert.Equal(1, result.AuthorsAdded);
        await using var v = NewDb(dbName);
        var author = await v.Authors.FirstOrDefaultAsync(a => a.OpenLibraryKey == "OL15295370A");
        Assert.NotNull(author);
        Assert.Equal("Francois Mauriac _OL15295370A", author!.CalibreFolderName); // durable folder link
        var file = await v.LocalBookFiles.FindAsync(1);
        Assert.Equal(author.Id, file!.AuthorId);   // claimed in place
    }

    private static LibraryDbContext NewDb(string name)
        => new(new DbContextOptionsBuilder<LibraryDbContext>().UseInMemoryDatabase(name).Options);
}

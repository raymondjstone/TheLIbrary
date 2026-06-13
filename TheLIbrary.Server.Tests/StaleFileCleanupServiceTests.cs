using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Scheduling;
using TheLibrary.Server.Services.Sync;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

public class StaleFileCleanupServiceTests
{
    [Fact]
    public async Task Prunes_Empty_And_Phantom_Folder_Rows_Only()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory("/lib");              // root is mounted
        fs.CreateDirectory("/lib/A/Empty");      // folder, no ebook inside
        // A folder that holds an ebook. Set forward-slash keys explicitly — the
        // fake's AddFile derives the parent via Path.GetDirectoryName, which
        // rewrites separators on Windows and wouldn't match the stored path.
        fs.CreateDirectory("/lib/A/HasBook");
        fs.ExistingFiles.Add("/lib/A/HasBook/in.pdf");
        fs.FilesByDirectory["/lib/A/HasBook"] = new List<string> { "/lib/A/HasBook/in.pdf" };
        // "/lib/A/Phantom" intentionally not created → missing folder under a mounted root.

        var dbName = NewDb();
        await using (var db = CreateDb(dbName))
        {
            db.LibraryLocations.Add(new LibraryLocation { Path = "/lib", Enabled = true });
            db.LocalBookFiles.AddRange(
                new LocalBookFile { Id = 1, FullPath = "/lib/A/book.epub" },     // file → keep
                new LocalBookFile { Id = 2, FullPath = "/lib/A/Empty" },         // empty folder → prune
                new LocalBookFile { Id = 3, FullPath = "/lib/A/HasBook" },       // folder w/ ebook → keep
                new LocalBookFile { Id = 4, FullPath = "/lib/A/Phantom" },       // missing folder → prune
                new LocalBookFile { Id = 5, FullPath = "/outside/Stray" });      // outside roots → keep
            await db.SaveChangesAsync();
        }

        var summary = await CreateService(fs, dbName).RunForTestsAsync(CancellationToken.None);

        Assert.Equal(2, summary.Pruned);
        await using var verify = CreateDb(dbName);
        var remaining = await verify.LocalBookFiles.Select(f => f.Id).OrderBy(i => i).ToListAsync();
        Assert.Equal(new[] { 1, 3, 5 }, remaining); // file, folder-with-book, outside-root survive
    }

    [Fact]
    public async Task Removes_Empty_Directories_But_Keeps_Nonempty_And_Protected_Ones()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory("/lib");                               // mounted root (protected)
        fs.AddDirectoryChild("/lib", "/lib/A");                   // author folder
        fs.AddDirectoryChild("/lib/A", "/lib/A/EmptyTitle");      // empty → remove
        fs.AddDirectoryChild("/lib/A", "/lib/A/RealTitle");       // holds a book → keep
        fs.ExistingFiles.Add("/lib/A/RealTitle/book.epub");
        fs.FilesByDirectory["/lib/A/RealTitle"] = new List<string> { "/lib/A/RealTitle/book.epub" };
        fs.AddDirectoryChild("/lib", "/lib/__unknown");           // empty but PROTECTED quarantine

        var dbName = NewDb();
        await using (var db = CreateDb(dbName))
        {
            db.LibraryLocations.Add(new LibraryLocation { Path = "/lib", Enabled = true });
            await db.SaveChangesAsync();
        }

        var summary = await CreateService(fs, dbName).RunForTestsAsync(CancellationToken.None);

        Assert.Equal(1, summary.EmptyFoldersRemoved);
        Assert.False(fs.DirectoryExists("/lib/A/EmptyTitle")); // empty leaf gone
        Assert.True(fs.DirectoryExists("/lib/A/RealTitle"));   // holds a file
        Assert.True(fs.DirectoryExists("/lib/A"));             // still has RealTitle
        Assert.True(fs.DirectoryExists("/lib/__unknown"));     // protected
        Assert.True(fs.DirectoryExists("/lib"));               // root never removed
    }

    [Fact]
    public async Task Aborts_When_No_Library_Root_Is_Mounted()
    {
        var fs = new FakeFileSystem(); // "/lib" is NOT created → not mounted

        var dbName = NewDb();
        await using (var db = CreateDb(dbName))
        {
            db.LibraryLocations.Add(new LibraryLocation { Path = "/lib", Enabled = true });
            db.LocalBookFiles.Add(new LocalBookFile { Id = 1, FullPath = "/lib/A/Empty" });
            await db.SaveChangesAsync();
        }

        var summary = await CreateService(fs, dbName).RunForTestsAsync(CancellationToken.None);

        Assert.Equal(0, summary.Pruned);
        await using var verify = CreateDb(dbName);
        Assert.Single(await verify.LocalBookFiles.ToListAsync()); // nothing deleted (mount safety)
    }

    private static string NewDb() => $"stale-tests-{Guid.NewGuid():N}";
    private static LibraryDbContext CreateDb(string name)
        => new(new DbContextOptionsBuilder<LibraryDbContext>().UseInMemoryDatabase(name).Options);

    private static StaleFileCleanupService CreateService(FakeFileSystem fs, string dbName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<LibraryDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();
        return new StaleFileCleanupService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new BackgroundTaskCoordinator(),
            fs,
            NullLogger<StaleFileCleanupService>.Instance);
    }
}

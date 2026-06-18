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

    [Fact]
    public async Task Removes_Live_Copies_That_Are_Already_In_The_Archive()
    {
        // The archive is a separate tree the scanner never reads. When something
        // re-drops an already-archived file back into the live library it looks new
        // and shows as a duplicate again. The sweep must delete the live copy whose
        // identical (same relative path + size) twin is confirmed in the archive,
        // and leave alone a live file that has no archive twin.
        var fs = new FakeFileSystem();
        fs.CreateDirectory("/Books/Collection");                 // mounted root
        // Archived twin (separate tree, outside the root) — present on disk.
        fs.ExistingFiles.Add("/Books/TheLibrary_Archive/Foster/Call to Arms/x.lit");
        // Live re-appearance, byte-identical to the archived twin → must be removed.
        fs.ExistingFiles.Add("/Books/Collection/Foster/Call to Arms/x.lit");
        fs.FilesByDirectory["/Books/Collection/Foster/Call to Arms"] = new() { "/Books/Collection/Foster/Call to Arms/x.lit" };
        // Live keeper with no archive twin → must survive.
        fs.ExistingFiles.Add("/Books/Collection/Foster/Damned/keep.epub");
        fs.FilesByDirectory["/Books/Collection/Foster/Damned"] = new() { "/Books/Collection/Foster/Damned/keep.epub" };

        var dbName = NewDb();
        await using (var db = CreateDb(dbName))
        {
            db.LibraryLocations.Add(new LibraryLocation { Path = "/Books/Collection", Enabled = true });
            db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.DedupeArchiveFolder, Value = "/Books/TheLibrary_Archive" });
            db.LocalBookFiles.AddRange(
                new LocalBookFile { Id = 1, FullPath = "/Books/TheLibrary_Archive/Foster/Call to Arms/x.lit", SizeBytes = 100 },
                new LocalBookFile { Id = 2, FullPath = "/Books/Collection/Foster/Call to Arms/x.lit", SizeBytes = 100 }, // re-appearance → delete
                new LocalBookFile { Id = 3, FullPath = "/Books/Collection/Foster/Damned/keep.epub", SizeBytes = 200 });   // keeper → keep
            await db.SaveChangesAsync();
        }

        var summary = await CreateService(fs, dbName).RunForTestsAsync(CancellationToken.None);

        Assert.Equal(1, summary.ReappearedArchivedRemoved);
        Assert.False(fs.FileExists("/Books/Collection/Foster/Call to Arms/x.lit")); // live re-appearance deleted
        Assert.True(fs.FileExists("/Books/TheLibrary_Archive/Foster/Call to Arms/x.lit")); // archive twin untouched
        Assert.True(fs.FileExists("/Books/Collection/Foster/Damned/keep.epub")); // keeper survives
        await using var verify = CreateDb(dbName);
        var remaining = await verify.LocalBookFiles.Select(f => f.Id).OrderBy(i => i).ToListAsync();
        Assert.Equal(new[] { 1, 3 }, remaining); // archive row + keeper row remain; re-appearance row gone
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

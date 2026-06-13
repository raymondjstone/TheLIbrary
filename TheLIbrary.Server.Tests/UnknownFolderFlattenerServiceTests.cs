using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Scheduling;
using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class UnknownFolderFlattenerServiceTests
{
    [Fact]
    public async Task Flatten_Moves_All_Files_To_The_Quarantine_Root_And_Removes_Author_Folders()
    {
        var root = Path.Combine(Path.GetTempPath(), $"flatten-tests-{Guid.NewGuid():N}");
        var dbName = $"flatten-tests-{Guid.NewGuid():N}";
        try
        {
            var unknownRoot = Path.Combine(root, "__unknown");
            var authorDir = Path.Combine(unknownRoot, "Quigley Fenwick");
            var nestedDir = Path.Combine(authorDir, "Some Title");
            var deeperDir = Path.Combine(nestedDir, "extras");
            Directory.CreateDirectory(deeperDir);
            var nestedFile = Path.Combine(nestedDir, "story.epub");
            var deeperFile = Path.Combine(deeperDir, "bonus.epub");
            var folderFlatFile = Path.Combine(authorDir, "already-flat.epub");
            File.WriteAllText(nestedFile, "x");
            File.WriteAllText(deeperFile, "x");
            File.WriteAllText(folderFlatFile, "x");

            var services = new ServiceCollection();
            services.AddDbContext<LibraryDbContext>(opt => opt.UseInMemoryDatabase(dbName));
            var provider = services.BuildServiceProvider();

            await using (var db = CreateDb(dbName))
            {
                db.LibraryLocations.Add(new LibraryLocation
                {
                    Id = 1, Path = root, IsPrimary = true, Enabled = true,
                    Label = "Test", CreatedAt = DateTime.UtcNow
                });
                db.LocalBookFiles.Add(new LocalBookFile
                {
                    Id = 1, AuthorFolder = "Quigley Fenwick", TitleFolder = "Some Title", FullPath = nestedFile
                });
                await db.SaveChangesAsync();
            }

            var sut = new UnknownFolderFlattenerService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                new BackgroundTaskCoordinator(),
                NullLogger<UnknownFolderFlattenerService>.Instance);

            Assert.True(sut.TryStart(CancellationToken.None, out var error), error);
            for (var i = 0; i < 200 && sut.IsRunning; i++) await Task.Delay(50);
            Assert.False(sut.IsRunning);

            var result = sut.LastResult;
            Assert.NotNull(result);
            Assert.Equal(1, result!.AuthorFoldersScanned);
            Assert.Equal(3, result.FilesMoved);             // all three lifted to the root
            Assert.Equal(3, result.DirectoriesRemoved);     // extras, Some Title, Quigley Fenwick
            Assert.Equal(1, result.DbRowsUpdated);

            // Everything now sits flat in the quarantine root.
            Assert.True(File.Exists(Path.Combine(unknownRoot, "story.epub")));
            Assert.True(File.Exists(Path.Combine(unknownRoot, "bonus.epub")));
            Assert.True(File.Exists(Path.Combine(unknownRoot, "already-flat.epub")));
            Assert.False(Directory.Exists(authorDir));      // the author folder is gone

            await using var verify = CreateDb(dbName);
            var row = await verify.LocalBookFiles.SingleAsync(f => f.Id == 1);
            Assert.Equal(Path.Combine(unknownRoot, "story.epub"), row.FullPath);
            Assert.Equal("story", row.TitleFolder);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Flatten_Suffixes_On_Filename_Collision_Instead_Of_Overwriting()
    {
        var root = Path.Combine(Path.GetTempPath(), $"flatten-tests-{Guid.NewGuid():N}");
        var dbName = $"flatten-tests-{Guid.NewGuid():N}";
        try
        {
            var unknownRoot = Path.Combine(root, "__unknown");
            var authorDir = Path.Combine(unknownRoot, "Quigley Fenwick");
            var nestedDir = Path.Combine(authorDir, "Some Title");
            Directory.CreateDirectory(nestedDir);
            File.WriteAllText(Path.Combine(authorDir, "story.epub"), "flat");
            File.WriteAllText(Path.Combine(nestedDir, "story.epub"), "nested");

            var services = new ServiceCollection();
            services.AddDbContext<LibraryDbContext>(opt => opt.UseInMemoryDatabase(dbName));
            var provider = services.BuildServiceProvider();

            await using (var db = CreateDb(dbName))
            {
                db.LibraryLocations.Add(new LibraryLocation
                {
                    Id = 1, Path = root, IsPrimary = true, Enabled = true,
                    Label = "Test", CreatedAt = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }

            var sut = new UnknownFolderFlattenerService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                new BackgroundTaskCoordinator(),
                NullLogger<UnknownFolderFlattenerService>.Instance);

            Assert.True(sut.TryStart(CancellationToken.None, out var error), error);
            for (var i = 0; i < 200 && sut.IsRunning; i++) await Task.Delay(50);
            Assert.False(sut.IsRunning);

            // Both copies land flat in the root under distinct names — neither lost.
            Assert.True(File.Exists(Path.Combine(unknownRoot, "story.epub")));
            Assert.True(File.Exists(Path.Combine(unknownRoot, "story_1.epub")));
            var contents = new[]
            {
                File.ReadAllText(Path.Combine(unknownRoot, "story.epub")),
                File.ReadAllText(Path.Combine(unknownRoot, "story_1.epub")),
            };
            Assert.Contains("flat", contents);
            Assert.Contains("nested", contents);
            Assert.False(Directory.Exists(authorDir));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static LibraryDbContext CreateDb(string name)
        => new(new DbContextOptionsBuilder<LibraryDbContext>().UseInMemoryDatabase(name).Options);
}

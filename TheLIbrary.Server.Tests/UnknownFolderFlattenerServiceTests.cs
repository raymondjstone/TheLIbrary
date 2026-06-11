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
    public async Task Flatten_Moves_Nested_Files_Up_And_Removes_Empty_Subdirs_And_Rewrites_Db_Rows()
    {
        var root = Path.Combine(Path.GetTempPath(), $"flatten-tests-{Guid.NewGuid():N}");
        var dbName = $"flatten-tests-{Guid.NewGuid():N}";
        try
        {
            var authorDir = Path.Combine(root, "__unknown", "Quigley Fenwick");
            var nestedDir = Path.Combine(authorDir, "Some Title");
            var deeperDir = Path.Combine(nestedDir, "extras");
            Directory.CreateDirectory(deeperDir);
            var nestedFile = Path.Combine(nestedDir, "story.epub");
            var deeperFile = Path.Combine(deeperDir, "bonus.epub");
            var flatFile = Path.Combine(authorDir, "already-flat.epub");
            File.WriteAllText(nestedFile, "x");
            File.WriteAllText(deeperFile, "x");
            File.WriteAllText(flatFile, "x");

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
                    Id = 1,
                    AuthorFolder = "Quigley Fenwick",
                    TitleFolder = "Some Title",
                    FullPath = nestedFile
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
            Assert.Equal(2, result.FilesMoved);
            Assert.Equal(2, result.DirectoriesRemoved);
            Assert.Equal(1, result.DbRowsUpdated);

            Assert.True(File.Exists(Path.Combine(authorDir, "story.epub")));
            Assert.True(File.Exists(Path.Combine(authorDir, "bonus.epub")));
            Assert.True(File.Exists(flatFile));
            Assert.False(Directory.Exists(nestedDir));

            await using var verify = CreateDb(dbName);
            var row = await verify.LocalBookFiles.SingleAsync(f => f.Id == 1);
            Assert.Equal(Path.Combine(authorDir, "story.epub"), row.FullPath);
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
            var authorDir = Path.Combine(root, "__unknown", "Quigley Fenwick");
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

            Assert.Equal("flat", File.ReadAllText(Path.Combine(authorDir, "story.epub")));
            Assert.Equal("nested", File.ReadAllText(Path.Combine(authorDir, "story_1.epub")));
            Assert.False(Directory.Exists(nestedDir));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static LibraryDbContext CreateDb(string name)
        => new(new DbContextOptionsBuilder<LibraryDbContext>().UseInMemoryDatabase(name).Options);
}

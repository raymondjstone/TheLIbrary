using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Scheduling;
using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class UnknownDuplicateRemovalServiceTests
{
    [Fact]
    public async Task Dedupe_Removes_Identical_Copies_Keeps_One_And_Cleans_Db_Rows()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dedupe-tests-{Guid.NewGuid():N}");
        var dbName = $"dedupe-tests-{Guid.NewGuid():N}";
        try
        {
            var unknownRoot = Path.Combine(root, "__unknown");
            var folderA = Path.Combine(unknownRoot, "Quigley Fenwick");
            var folderB = Path.Combine(unknownRoot, "Mysterious Drop");
            Directory.CreateDirectory(folderA);
            Directory.CreateDirectory(folderB);

            // Three byte-identical copies under different names and folders —
            // the root-level copy has the shortest path and must survive.
            var keeper = Path.Combine(unknownRoot, "story.epub");
            var dupSameName = Path.Combine(folderA, "story.epub");
            var dupOtherName = Path.Combine(folderB, "renamed copy.epub");
            File.WriteAllText(keeper, "identical book contents");
            File.WriteAllText(dupSameName, "identical book contents");
            File.WriteAllText(dupOtherName, "identical book contents");

            // Same size as the duplicates but different bytes — must stay.
            var sameSizeDifferent = Path.Combine(folderA, "other tale.epub");
            File.WriteAllText(sameSizeDifferent, "DIFFERENT book content!");
            Assert.Equal(new FileInfo(keeper).Length, new FileInfo(sameSizeDifferent).Length);

            // Zero-byte file — junk, deleted outright.
            var emptyFile = Path.Combine(folderA, "broken download.lit");
            File.WriteAllText(emptyFile, "");

            var provider = BuildProvider(dbName);
            await using (var db = CreateDb(dbName))
            {
                db.LibraryLocations.Add(new LibraryLocation
                {
                    Id = 1, Path = root, IsPrimary = true, Enabled = true,
                    Label = "Test", CreatedAt = DateTime.UtcNow
                });
                db.UnknownFiles.Add(new UnknownFile { Id = 1, FullPath = dupSameName, FileName = "story.epub" });
                db.UnknownFiles.Add(new UnknownFile { Id = 2, FullPath = keeper, FileName = "story.epub" });
                db.LocalBookFiles.Add(new LocalBookFile { Id = 1, FullPath = dupOtherName });
                await db.SaveChangesAsync();
            }

            var sut = new UnknownDuplicateRemovalService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                new BackgroundTaskCoordinator(),
                NullLogger<UnknownDuplicateRemovalService>.Instance);

            Assert.True(sut.TryStart(CancellationToken.None, out var error), error);
            for (var i = 0; i < 200 && sut.IsRunning; i++) await Task.Delay(50);
            Assert.False(sut.IsRunning);

            var result = sut.LastResult;
            Assert.NotNull(result);
            Assert.Equal(5, result!.FilesScanned);
            Assert.Equal(4, result.FilesHashed); // all four non-empty files share one size
            Assert.Equal(0, result.HashFailures);
            Assert.Equal(1, result.DuplicateGroups);
            Assert.Equal(2, result.FilesDeleted);
            Assert.Equal(1, result.EmptyFilesDeleted);
            Assert.Equal(2, result.DbRowsRemoved); // dupSameName UnknownFile + dupOtherName LocalBookFile

            Assert.True(File.Exists(keeper));
            Assert.True(File.Exists(sameSizeDifferent));
            Assert.False(File.Exists(dupSameName));
            Assert.False(File.Exists(dupOtherName));
            Assert.False(File.Exists(emptyFile));

            await using var verify = CreateDb(dbName);
            var unknownRow = Assert.Single(verify.UnknownFiles);
            Assert.Equal(keeper, unknownRow.FullPath);
            Assert.Empty(verify.LocalBookFiles);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Dedupe_Leaves_Same_Name_Different_Content_Files_Alone()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dedupe-tests-{Guid.NewGuid():N}");
        var dbName = $"dedupe-tests-{Guid.NewGuid():N}";
        try
        {
            var unknownRoot = Path.Combine(root, "__unknown");
            var folderA = Path.Combine(unknownRoot, "Quigley Fenwick");
            var folderB = Path.Combine(unknownRoot, "Mysterious Drop");
            Directory.CreateDirectory(folderA);
            Directory.CreateDirectory(folderB);

            // Same name, same size, different bytes — not duplicates.
            var fileA = Path.Combine(folderA, "story.epub");
            var fileB = Path.Combine(folderB, "story.epub");
            File.WriteAllText(fileA, "the first book text");
            File.WriteAllText(fileB, "a different book!!!");

            var provider = BuildProvider(dbName);
            await using (var db = CreateDb(dbName))
            {
                db.LibraryLocations.Add(new LibraryLocation
                {
                    Id = 1, Path = root, IsPrimary = true, Enabled = true,
                    Label = "Test", CreatedAt = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }

            var sut = new UnknownDuplicateRemovalService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                new BackgroundTaskCoordinator(),
                NullLogger<UnknownDuplicateRemovalService>.Instance);

            Assert.True(sut.TryStart(CancellationToken.None, out var error), error);
            for (var i = 0; i < 200 && sut.IsRunning; i++) await Task.Delay(50);
            Assert.False(sut.IsRunning);

            var result = sut.LastResult;
            Assert.NotNull(result);
            Assert.Equal(0, result!.DuplicateGroups);
            Assert.Equal(0, result.FilesDeleted);
            Assert.True(File.Exists(fileA));
            Assert.True(File.Exists(fileB));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ChooseKeeper_Prefers_Shortest_Path_Then_Alphabetical()
    {
        Assert.Equal(@"C:\u\a.epub", UnknownDuplicateRemovalService.ChooseKeeper(new[]
        {
            @"C:\u\sub\a.epub",
            @"C:\u\a.epub",
            @"C:\u\a_1.epub",
        }));

        Assert.Equal(@"C:\u\a.epub", UnknownDuplicateRemovalService.ChooseKeeper(new[]
        {
            @"C:\u\b.epub",
            @"C:\u\a.epub",
        }));
    }

    private static ServiceProvider BuildProvider(string dbName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<LibraryDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        return services.BuildServiceProvider();
    }

    private static LibraryDbContext CreateDb(string name)
        => new(new DbContextOptionsBuilder<LibraryDbContext>().UseInMemoryDatabase(name).Options);
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Incoming;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

public class IncomingProcessorTests
{
    [Fact]
    public async Task ProcessAsync_Throws_When_Incoming_Folder_Is_Missing()
    {
        await using var db = CreateDb();
        db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.IncomingFolder, Value = "C:\\incoming" });
        db.LibraryLocations.Add(new LibraryLocation { Id = 1, Path = "C:\\library", IsPrimary = true, Enabled = true, Label = "Default", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var fs = new FakeFileSystem();
        fs.ExistingDirectories.Add("C:\\library");
        var sut = new IncomingProcessor(db, fs, NullLogger<IncomingProcessor>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ProcessAsync(CancellationToken.None));
        Assert.Contains("Incoming folder does not exist", ex.Message);
    }

    [Fact]
    public async Task ProcessUnknownAsync_Returns_Empty_Result_When_Unknown_Folder_Does_Not_Exist()
    {
        await using var db = CreateDb();
        db.LibraryLocations.Add(new LibraryLocation { Id = 1, Path = "C:\\library", IsPrimary = true, Enabled = true, Label = "Default", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var fs = new FakeFileSystem();
        fs.ExistingDirectories.Add("C:\\library");
        var sut = new IncomingProcessor(db, fs, NullLogger<IncomingProcessor>.Instance);

        var result = await sut.ProcessUnknownAsync(null, CancellationToken.None);

        Assert.Equal(0, result.Processed);
        Assert.Single(result.Log);
    }

    [Fact]
    public async Task ProcessUnknownAsync_Leaves_Unmatched_File_In_Place()
    {
        await using var db = CreateDb();
        db.LibraryLocations.Add(new LibraryLocation { Id = 1, Path = "C:\\library", IsPrimary = true, Enabled = true, Label = "Default", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var fs = new FakeFileSystem();
        fs.CreateDirectory("C:\\library");
        fs.CreateDirectory("C:\\library\\__unknown");
        fs.AddFile("C:\\library\\__unknown\\mystery.epub");
        fs.FilesByDirectory["C:\\library\\__unknown"] = ["C:\\library\\__unknown\\mystery.epub"];
        var sut = new IncomingProcessor(db, fs, NullLogger<IncomingProcessor>.Instance);

        var result = await sut.ProcessUnknownAsync(null, CancellationToken.None);

        Assert.Equal(1, result.Processed);
        Assert.Equal(1, result.UnknownAuthor);
        Assert.Contains(result.Log, line => line.Contains("still unmatched", StringComparison.OrdinalIgnoreCase));
        Assert.True(fs.FileExists("C:\\library\\__unknown\\mystery.epub"));
    }

    [Fact]
    public async Task ProcessAsync_Moves_Unmatched_File_Into_Unknown_Folder_And_Cleans_Source_Directory()
    {
        await using var db = CreateDb();
        db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.IncomingFolder, Value = "C:\\incoming" });
        db.LibraryLocations.Add(new LibraryLocation { Id = 1, Path = "C:\\library", IsPrimary = true, Enabled = true, Label = "Default", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var fs = new FakeFileSystem();
        fs.CreateDirectory("C:\\incoming");
        fs.CreateDirectory("C:\\library");
        fs.AddDirectoryChild("C:\\incoming", "C:\\incoming\\drop");
        fs.AddFile("C:\\incoming\\drop\\mystery.bin");

        var sut = new IncomingProcessor(db, fs, NullLogger<IncomingProcessor>.Instance);

        var result = await sut.ProcessAsync(CancellationToken.None);

        Assert.Equal(1, result.Processed);
        Assert.Equal(1, result.UnknownAuthor);
        Assert.True(fs.FileExists("C:\\library\\__unknown\\mystery.bin"));
        Assert.False(fs.FileExists("C:\\incoming\\drop\\mystery.bin"));
        Assert.False(fs.DirectoryExists("C:\\incoming\\drop"));
    }

    [Fact]
    public async Task ProcessAsync_Unmatched_Nested_File_Lands_Directly_Under_Top_Level_Unknown_Folder()
    {
        await using var db = CreateDb();
        db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.IncomingFolder, Value = "C:\\incoming" });
        db.LibraryLocations.Add(new LibraryLocation { Id = 1, Path = "C:\\library", IsPrimary = true, Enabled = true, Label = "Default", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var fs = new FakeFileSystem();
        fs.CreateDirectory("C:\\incoming");
        fs.CreateDirectory("C:\\library");
        fs.AddDirectoryChild("C:\\incoming", "C:\\incoming\\drop");
        fs.AddDirectoryChild("C:\\incoming\\drop", "C:\\incoming\\drop\\some title");
        fs.AddFile("C:\\incoming\\drop\\some title\\mystery.bin");

        var sut = new IncomingProcessor(db, fs, NullLogger<IncomingProcessor>.Instance);

        var result = await sut.ProcessAsync(CancellationToken.None);

        Assert.Equal(1, result.Processed);
        Assert.Equal(1, result.UnknownAuthor);
        Assert.True(fs.FileExists("C:\\library\\__unknown\\mystery.bin"));
        Assert.False(fs.FileExists("C:\\library\\__unknown\\drop\\mystery.bin"));
        Assert.False(fs.FileExists("C:\\library\\__unknown\\drop\\some title\\mystery.bin"));
    }

    [Fact]
    public async Task ProcessAsync_Unmatched_Same_Filename_From_Different_Subfolders_Is_Suffixed_Not_Overwritten()
    {
        await using var db = CreateDb();
        db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.IncomingFolder, Value = "C:\\incoming" });
        db.LibraryLocations.Add(new LibraryLocation { Id = 1, Path = "C:\\library", IsPrimary = true, Enabled = true, Label = "Default", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var fs = new FakeFileSystem();
        fs.CreateDirectory("C:\\incoming");
        fs.CreateDirectory("C:\\library");
        fs.AddDirectoryChild("C:\\incoming", "C:\\incoming\\drop");
        fs.AddDirectoryChild("C:\\incoming\\drop", "C:\\incoming\\drop\\title one");
        fs.AddDirectoryChild("C:\\incoming\\drop", "C:\\incoming\\drop\\title two");
        fs.AddFile("C:\\incoming\\drop\\title one\\mystery.bin");
        fs.AddFile("C:\\incoming\\drop\\title two\\mystery.bin");

        var sut = new IncomingProcessor(db, fs, NullLogger<IncomingProcessor>.Instance);

        var result = await sut.ProcessAsync(CancellationToken.None);

        Assert.Equal(2, result.Processed);
        Assert.Equal(2, result.UnknownAuthor);
        Assert.True(fs.FileExists("C:\\library\\__unknown\\mystery.bin"));
        Assert.True(fs.FileExists("C:\\library\\__unknown\\mystery_1.bin"));
    }

    [Fact]
    public async Task ProcessAsync_Deletes_Junk_File_And_Cleans_Empty_Directory()
    {
        await using var db = CreateDb();
        db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.IncomingFolder, Value = "C:\\incoming" });
        db.LibraryLocations.Add(new LibraryLocation { Id = 1, Path = "C:\\library", IsPrimary = true, Enabled = true, Label = "Default", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var fs = new FakeFileSystem();
        fs.CreateDirectory("C:\\incoming");
        fs.CreateDirectory("C:\\library");
        fs.AddDirectoryChild("C:\\incoming", "C:\\incoming\\covers");
        fs.AddFile("C:\\incoming\\covers\\cover.jpg");

        var sut = new IncomingProcessor(db, fs, NullLogger<IncomingProcessor>.Instance);

        var result = await sut.ProcessAsync(CancellationToken.None);

        Assert.Equal(0, result.Processed);
        Assert.False(fs.FileExists("C:\\incoming\\covers\\cover.jpg"));
        Assert.False(fs.DirectoryExists("C:\\incoming\\covers"));
        Assert.Contains(result.Log, line => line.Contains("deleted junk", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProcessAsync_Tracked_Author_Without_OL_Key_Still_Gets_File()
    {
        // Regression: a tracked (watchlist) author whose OL key hasn't been
        // resolved yet must still receive their files. Previously the code
        // returned null and routed to __unknown, making the watchlist useless
        // for authors that aren't in the OL catalogue or haven't been seeded.
        await using var db = CreateDb();
        db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.IncomingFolder, Value = "C:\\incoming" });
        db.LibraryLocations.Add(new LibraryLocation
        {
            Id = 1, Path = "C:\\library", IsPrimary = true,
            Enabled = true, Label = "Default", CreatedAt = DateTime.UtcNow
        });
        db.Authors.Add(new Author
        {
            Id = 1, Name = "Michael Todd", CalibreFolderName = "Michael Todd",
            OpenLibraryKey = null, Status = AuthorStatus.Active
        });
        await db.SaveChangesAsync();

        var fs = new FakeFileSystem();
        fs.CreateDirectory("C:\\incoming");
        fs.CreateDirectory("C:\\library");
        fs.AddDirectoryChild("C:\\incoming", "C:\\incoming\\drop");
        fs.AddFile("C:\\incoming\\drop\\Backstabbing Little Assets - Michael Todd.epub");

        var sut = new IncomingProcessor(db, fs, NullLogger<IncomingProcessor>.Instance);
        var result = await sut.ProcessAsync(CancellationToken.None);

        Assert.Equal(1, result.Processed);
        Assert.Equal(1, result.Matched);
        Assert.Equal(0, result.UnknownAuthor);
        // File must be under the author's folder, not __unknown.
        Assert.False(fs.ExistingFiles.Any(f =>
            f.Contains("__unknown", StringComparison.OrdinalIgnoreCase)));
        Assert.True(fs.ExistingFiles.Any(f =>
            f.Contains("Michael Todd", StringComparison.OrdinalIgnoreCase)));
    }

    private static LibraryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<LibraryDbContext>()
            .UseInMemoryDatabase($"incoming-tests-{Guid.NewGuid():N}")
            .Options;
        return new LibraryDbContext(options);
    }
}

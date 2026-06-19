using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Scheduling;
using TheLibrary.Server.Services.Sync;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

public class SeriesOrganizerServiceTests
{
    [Fact]
    public void MoveSingleFileForTests_Moves_File_And_Updates_TitleFolder()
    {
        var fs = new FakeFileSystem();
        fs.AddFile("C:\\lib\\Author\\old.epub", [1, 2, 3]);
        var sut = new SeriesOrganizerService(CreateScopeFactory(), new BackgroundTaskCoordinator(), fs, NullLogger<SeriesOrganizerService>.Instance);
        var file = new LocalBookFile { FullPath = "C:\\lib\\Author\\old.epub", TitleFolder = "old" };

        var path = sut.MoveSingleFileForTests(file, "C:\\lib\\Author\\Series");

        Assert.Equal("C:\\lib\\Author\\Series\\old.epub", path);
        Assert.Equal("old", file.TitleFolder);
        Assert.True(fs.FileExists(path));
        Assert.False(fs.FileExists("C:\\lib\\Author\\old.epub"));
    }

    // CIFS/NFS regression: File.Move can copy to the destination but leave the
    // source on disk (deferred/failed unlink). The organizer must force-remove the
    // lingering source, otherwise the next sync scan re-imports it and the book
    // reappears as a duplicate with no new files added.
    [Fact]
    public void MoveSingleFileForTests_Force_Removes_Source_When_Move_Leaves_It_Behind()
    {
        var fs = new FakeFileSystem();
        fs.AddFile("C:\\lib\\Author\\old.epub", [1, 2, 3]);
        fs.MoveLeavesSource.Add("C:\\lib\\Author\\old.epub"); // simulate the deferred-delete mount
        var sut = new SeriesOrganizerService(CreateScopeFactory(), new BackgroundTaskCoordinator(), fs, NullLogger<SeriesOrganizerService>.Instance);
        var file = new LocalBookFile { FullPath = "C:\\lib\\Author\\old.epub", TitleFolder = "old" };

        var path = sut.MoveSingleFileForTests(file, "C:\\lib\\Author\\Series");

        Assert.True(fs.FileExists(path));
        // The lingering source must be gone — nothing for the scanner to re-import.
        Assert.False(fs.FileExists("C:\\lib\\Author\\old.epub"));
    }

    [Fact]
    public void DeleteEmptyAncestorsForTests_Removes_Empty_Intermediate_Folders()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory("C:\\lib\\Author");
        fs.AddDirectoryChild("C:\\lib\\Author", "C:\\lib\\Author\\OldSeries");
        fs.AddDirectoryChild("C:\\lib\\Author\\OldSeries", "C:\\lib\\Author\\OldSeries\\Title");
        var sut = new SeriesOrganizerService(CreateScopeFactory(), new BackgroundTaskCoordinator(), fs, NullLogger<SeriesOrganizerService>.Instance);

        sut.DeleteEmptyAncestorsForTests("C:\\lib\\Author\\OldSeries\\Title", "C:\\lib\\Author", "C:\\lib\\Author\\NewSeries");

        Assert.False(fs.DirectoryExists("C:\\lib\\Author\\OldSeries\\Title"));
        Assert.False(fs.DirectoryExists("C:\\lib\\Author\\OldSeries"));
        Assert.True(fs.DirectoryExists("C:\\lib\\Author"));
    }

    private static IServiceScopeFactory CreateScopeFactory()
    {
        var services = new ServiceCollection();
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }
}

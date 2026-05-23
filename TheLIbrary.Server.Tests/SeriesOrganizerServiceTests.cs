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

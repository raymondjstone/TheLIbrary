using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Scheduling;
using TheLibrary.Server.Services.Sync;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

public class AuthorFolderDisambiguatorServiceTests
{
    [Fact]
    public void MoveFileToFolderForTests_Moves_File_Into_New_Author_Folder()
    {
        var fs = new FakeFileSystem();
        fs.AddFile("C:\\lib\\Shared\\book.epub", [1, 2, 3]);
        var sut = new AuthorFolderDisambiguatorService(CreateScopeFactory(), new BackgroundTaskCoordinator(), fs, NullLogger<AuthorFolderDisambiguatorService>.Instance);
        var file = new LocalBookFile
        {
            AuthorFolder = "Shared",
            FullPath = "C:\\lib\\Shared\\book.epub"
        };

        sut.MoveFileToFolderForTests(
            file,
            new Author { Id = 2 },
            "Author_OL2A",
            ["C:\\lib"],
            []);

        Assert.Equal(2, file.AuthorId);
        Assert.Equal("Author_OL2A", file.AuthorFolder);
        Assert.Equal("C:\\lib\\Author_OL2A\\book.epub", file.FullPath);
        Assert.True(fs.FileExists("C:\\lib\\Author_OL2A\\book.epub"));
    }

    [Fact]
    public void MoveFileToFolderForTests_Records_Warning_On_Name_Conflict()
    {
        var fs = new FakeFileSystem();
        fs.AddFile("C:\\lib\\Shared\\book.epub", [1, 2, 3]);
        fs.AddFile("C:\\lib\\Author_OL2A\\book.epub", [9, 9, 9]);
        var sut = new AuthorFolderDisambiguatorService(CreateScopeFactory(), new BackgroundTaskCoordinator(), fs, NullLogger<AuthorFolderDisambiguatorService>.Instance);
        var file = new LocalBookFile
        {
            AuthorFolder = "Shared",
            FullPath = "C:\\lib\\Shared\\book.epub"
        };
        var warnings = new List<string>();

        sut.MoveFileToFolderForTests(
            file,
            new Author { Id = 2 },
            "Author_OL2A",
            ["C:\\lib"],
            warnings);

        Assert.Equal("C:\\lib\\Author_OL2A\\book_2.epub", file.FullPath);
        Assert.Empty(warnings);
    }

    private static IServiceScopeFactory CreateScopeFactory()
    {
        var services = new ServiceCollection();
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }
}

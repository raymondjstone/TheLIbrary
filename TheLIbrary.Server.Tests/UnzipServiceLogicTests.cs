using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class UnzipServiceLogicTests
{
    [Theory]
    [InlineData("C:/books/archive.zip", false)]
    [InlineData("C:/books/archive.rar", false)]
    [InlineData("C:/books/book.epub", true)]
    [InlineData("\\\\server\\share\\books\\archive.zip", false)]
    public void ShouldSkipFileForTests_Recognizes_Archives(string path, bool expected)
    {
        Assert.Equal(expected, UnzipService.ShouldSkipFileForTests(path));
    }
}

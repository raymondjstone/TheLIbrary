using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class UnzipServiceTests
{
    [Fact]
    public void ResolveEffectivePathForTests_Strips_Unc_Server_And_Share()
    {
        var result = UnzipService.ResolveEffectivePathForTests("\\\\server\\share\\books\\archive.zip");

        Assert.Equal("/books/archive.zip", result);
    }

    [Fact]
    public void ResolveEffectivePathForTests_Leaves_Normal_Path_Alone()
    {
        var path = "C:/books/archive.zip";

        var result = UnzipService.ResolveEffectivePathForTests(path);

        Assert.Equal(path, result);
    }

    [Fact]
    public void UniqueDestinationPathForTests_Adds_Suffix_When_File_Exists()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"unzip-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            File.WriteAllText(Path.Combine(temp, "book.epub"), "x");

            var result = UnzipService.UniqueDestinationPathForTests(temp, "book.epub");

            Assert.EndsWith("book_1.epub", result, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }
}

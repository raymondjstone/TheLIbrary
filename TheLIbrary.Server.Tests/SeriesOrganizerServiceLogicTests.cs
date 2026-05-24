using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class SeriesOrganizerServiceLogicTests
{
    [Fact]
    public void ResolveSeriesNameForTests_Cleans_Stored_TitleFolder_Like_Value()
    {
        var result = SeriesOrganizerService.ResolveSeriesNameForTests("Iron March 02 - The Broken Skiff", 12, "ignored");

        Assert.Equal("Iron March", result);
    }

    [Fact]
    public void ResolveSeriesNameForTests_Parses_From_Filename_When_No_Series_Is_Set()
    {
        var result = SeriesOrganizerService.ResolveSeriesNameForTests(null, null, "Iron March 02 - The Broken Skiff");

        Assert.Equal("Iron March", result);
    }

    [Fact]
    public void ResolveSeriesNameForTests_Returns_Null_When_No_Series_Can_Be_Determined()
    {
        var result = SeriesOrganizerService.ResolveSeriesNameForTests(null, null, "Standalone Title");

        Assert.Null(result);
    }

    [Fact]
    public void ComputeTargetDirForTests_Uses_Author_Root_When_Series_Is_Blank()
    {
        var result = SeriesOrganizerService.ComputeTargetDirForTests("C:\\lib", "Author", null);

        Assert.Equal("C:\\lib\\Author", result);
    }

    [Fact]
    public void ComputeTargetDirForTests_Uses_Sanitized_Series_Subfolder_When_Series_Is_Present()
    {
        var result = SeriesOrganizerService.ComputeTargetDirForTests("C:\\lib", "Author", "Bad:Series");

        Assert.Equal(Path.Combine("C:\\lib\\Author", SeriesOrganizerService.SanitizeFolderName("Bad:Series")), result);
    }
}

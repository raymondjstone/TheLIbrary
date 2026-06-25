using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class ReadEditionPropagationServiceTests
{
    // Tuple shape: (Id, AuthorId, NormalizedTitle, IsRead)

    [Fact]
    public void Marks_Sibling_Editions_Read_When_One_Is_Read()
    {
        var ids = ReadEditionPropagationService.SelectIdsToMark(
        [
            (1, 10, "abc", true),    // the read edition
            (2, 10, "abc", false),   // sibling → mark read
            (3, 10, "abc", false),   // sibling → mark read
        ]);

        Assert.Equal([2, 3], ids);
    }

    [Fact]
    public void Leaves_The_Already_Read_Edition_Alone()
    {
        var ids = ReadEditionPropagationService.SelectIdsToMark(
        [
            (1, 10, "abc", true),
            (2, 10, "abc", true),
        ]);

        Assert.Empty(ids);
    }

    [Fact]
    public void Ignores_Titles_Where_No_Edition_Is_Read()
    {
        var ids = ReadEditionPropagationService.SelectIdsToMark(
        [
            (1, 10, "abc", false),
            (2, 10, "abc", false),
        ]);

        Assert.Empty(ids);
    }

    [Fact]
    public void Does_Not_Cross_Authors_Even_With_The_Same_Title()
    {
        var ids = ReadEditionPropagationService.SelectIdsToMark(
        [
            (1, 10, "abc", true),
            (2, 20, "abc", false),   // same title, different author → leave alone
        ]);

        Assert.Empty(ids);
    }

    [Fact]
    public void Skips_Entries_With_No_Normalized_Title()
    {
        var ids = ReadEditionPropagationService.SelectIdsToMark(
        [
            (1, 10, null, true),
            (2, 10, null, false),    // null title can't be grouped → skip
            (3, 10, "",   false),    // empty title likewise
        ]);

        Assert.Empty(ids);
    }
}

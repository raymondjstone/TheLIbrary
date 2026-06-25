using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class OtherEditionMarkerServiceTests
{
    // Tuple shape: (Id, AuthorId, NormalizedTitle, HasFile, OwnedDifferentEdition)

    [Fact]
    public void Marks_Fileless_Siblings_When_One_Edition_Has_An_Ebook()
    {
        var ids = OtherEditionMarkerService.SelectIdsToMark(
        [
            (1, 10, "abc", true,  false),   // has the ebook
            (2, 10, "abc", false, false),   // duplicate without a file → mark
            (3, 10, "abc", false, false),   // duplicate without a file → mark
        ]);

        Assert.Equal([2, 3], ids);
    }

    [Fact]
    public void Leaves_The_Edition_That_Has_The_File_Alone()
    {
        var ids = OtherEditionMarkerService.SelectIdsToMark(
        [
            (1, 10, "abc", true, false),
            (2, 10, "abc", true, false),
        ]);

        Assert.Empty(ids);
    }

    [Fact]
    public void Ignores_Titles_Where_No_Edition_Has_A_File()
    {
        var ids = OtherEditionMarkerService.SelectIdsToMark(
        [
            (1, 10, "abc", false, false),
            (2, 10, "abc", false, false),
        ]);

        Assert.Empty(ids);
    }

    [Fact]
    public void Does_Not_Cross_Authors_Even_With_The_Same_Title()
    {
        var ids = OtherEditionMarkerService.SelectIdsToMark(
        [
            (1, 10, "abc", true,  false),
            (2, 20, "abc", false, false),   // same title, different author → leave alone
        ]);

        Assert.Empty(ids);
    }

    [Fact]
    public void Is_Idempotent_Skipping_Already_Flagged_Rows()
    {
        var ids = OtherEditionMarkerService.SelectIdsToMark(
        [
            (1, 10, "abc", true,  false),
            (2, 10, "abc", false, true),    // already "other edition" → skip
            (3, 10, "abc", false, false),   // still needs marking
        ]);

        Assert.Equal([3], ids);
    }

    [Fact]
    public void Skips_Entries_With_No_Normalized_Title()
    {
        var ids = OtherEditionMarkerService.SelectIdsToMark(
        [
            (1, 10, null, true,  false),
            (2, 10, null, false, false),    // null title can't be grouped → skip
            (3, 10, "",   false, false),    // empty title likewise
        ]);

        Assert.Empty(ids);
    }
}

using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

// The archive holding folder must be inert: once a duplicate is archived, no job
// may scan, move, re-link, or delete it. Every job relies on these predicates to
// recognise an archived path, so they're pinned here for both the bare-leaf
// ("__archive") and absolute-path ("/Books/TheLibrary_Archive") configurations.
public class ArchivePolicyTests
{
    [Theory]
    [InlineData("/Books/__archive/Gregor Vance Mallory/A Tide of Ashes.epub", true)]
    [InlineData("/Books/Gregor Vance Mallory/A Tide of Ashes.epub", false)]
    // Only a whole path component named "__archive" counts — not a substring.
    [InlineData("/Books/my__archived books/x.epub", false)]
    [InlineData("/Books/Author/__archive notes/x.epub", false)]
    public void IsUnder_Leaf_Matches_Path_Component_Only(string path, bool expected)
        => Assert.Equal(expected, ArchivePolicy.IsUnder(path, "__archive"));

    [Fact]
    public void IsUnder_Is_Case_Insensitive_And_Slash_Normalised()
    {
        Assert.True(ArchivePolicy.IsUnder("/Books/__ARCHIVE/x.epub", "__archive"));
        Assert.True(ArchivePolicy.IsUnder("\\Books\\__archive\\x.epub", "__archive"));
    }

    [Fact]
    public void IsUnder_AbsolutePath_Matches_By_Prefix()
    {
        const string leaf = "/Books/TheLibrary_Archive";
        Assert.True(ArchivePolicy.IsUnder("/Books/TheLibrary_Archive/Author/x.epub", leaf));
        Assert.False(ArchivePolicy.IsUnder("/Books/Author/x.epub", leaf));
    }

    [Theory]
    [InlineData("__archive", "__archive")]
    [InlineData("/Books/TheLibrary_Archive", "TheLibrary_Archive")]
    [InlineData("/Books/TheLibrary_Archive/", "TheLibrary_Archive")]
    public void FolderName_Returns_Final_Segment(string configured, string expected)
        => Assert.Equal(expected, ArchivePolicy.FolderName(configured));

    [Fact]
    public void NotUnder_Predicate_Excludes_Archived_Rows()
    {
        var rows = new[]
        {
            new LocalBookFile { FullPath = "/Books/Gregor Vance Mallory/A Tide of Ashes.epub" },
            new LocalBookFile { FullPath = "/Books/__archive/Gregor Vance Mallory/A Tide of Ashes.epub" },
        };

        var kept = rows.AsQueryable().Where(ArchivePolicy.NotUnder("__archive")).ToList();

        Assert.Single(kept);
        Assert.DoesNotContain(kept, f => f.FullPath.Contains("__archive"));
    }
}

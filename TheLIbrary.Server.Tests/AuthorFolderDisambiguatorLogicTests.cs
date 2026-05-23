using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class AuthorFolderDisambiguatorLogicTests
{
    [Fact]
    public void FindGroupsForTests_Returns_Only_Fully_Keyed_Collision_Groups()
    {
        var authors = new List<Author>
        {
            new() { Id = 1, Name = "Same Name", OpenLibraryKey = "OL1A" },
            new() { Id = 2, Name = "Same Name", OpenLibraryKey = "OL2A" },
            new() { Id = 3, Name = "Other Name", OpenLibraryKey = "OL3A" },
            new() { Id = 4, Name = "Other Name", OpenLibraryKey = null }
        };

        var groups = AuthorFolderDisambiguatorService.FindGroupsForTests(authors);

        var group = Assert.Single(groups);
        Assert.Equal([1, 2], group.Select(a => a.Id).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void ResolveOwnerForTests_Returns_Matching_Author_When_Title_Is_Known()
    {
        var owner = AuthorFolderDisambiguatorService.ResolveOwnerForTests(
            new LocalBookFile { NormalizedTitle = "known title" },
            new Dictionary<string, int>(StringComparer.Ordinal) { ["known title"] = 5 },
            new Author { Id = 1 });

        Assert.Equal(5, owner.Id);
    }

    [Fact]
    public void ResolveOwnerForTests_Falls_Back_When_Title_Is_Unknown()
    {
        var owner = AuthorFolderDisambiguatorService.ResolveOwnerForTests(
            new LocalBookFile { NormalizedTitle = "missing" },
            new Dictionary<string, int>(StringComparer.Ordinal),
            new Author { Id = 1 });

        Assert.Equal(1, owner.Id);
    }
}

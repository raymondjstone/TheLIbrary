using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class AuthorFolderNameResolverMoreTests
{
    private static Author A(int id, string name, string? olKey = null, int? linkedTo = null) => new()
    {
        Id = id,
        Name = name,
        OpenLibraryKey = olKey,
        LinkedToAuthorId = linkedTo
    };

    [Fact]
    public void Resolve_Uses_Original_Name_When_Normalized_Name_Is_Empty()
    {
        var author = A(1, "!!!", "OL1A");

        var resolved = AuthorFolderNameResolver.Resolve(author, new[] { author });

        Assert.Equal("!!!", resolved);
    }

    [Fact]
    public void FindCollisionGroup_Includes_Self_When_Others_Are_Linked()
    {
        var authors = new[]
        {
            A(1, "John Smith", "OL1A"),
            A(2, "John Smith", "OL2A", linkedTo: 1)
        };

        var group = AuthorFolderNameResolver.FindCollisionGroup(authors[0], authors);

        Assert.Single(group);
        Assert.Equal(1, group[0].Id);
    }

    [Fact]
    public void Resolve_Is_CaseInsensitive_For_Name_Collisions()
    {
        var authors = new[]
        {
            A(1, "John Smith", "OL1A"),
            A(2, "john smith", "OL2A")
        };

        Assert.Equal("John Smith_OL1A", AuthorFolderNameResolver.Resolve(authors[0], authors));
        Assert.Equal("john smith_OL2A", AuthorFolderNameResolver.Resolve(authors[1], authors));
    }

    [Fact]
    public void Resolve_Preserves_Bare_Name_For_Solo_Author_Without_OlKey()
    {
        var author = A(1, "Solo Author", null);

        var resolved = AuthorFolderNameResolver.Resolve(author, new[] { author });

        Assert.Equal("Solo Author", resolved);
    }
}

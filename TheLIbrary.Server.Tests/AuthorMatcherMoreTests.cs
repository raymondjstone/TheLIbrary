using TheLibrary.Server.Services.Incoming;
using Xunit;

namespace TheLibrary.Server.Tests;

public class AuthorMatcherMoreTests
{
    private static AuthorIndexEntry Tracked(string name, string? folder = null, int? id = 1, IReadOnlyList<string>? aliases = null) =>
        new(name, folder ?? name, true, id, AlternateNames: aliases);

    private static AuthorIndexEntry Ol(string name, string? folder = null) =>
        new(name, folder ?? name, false);

    [Fact]
    public void IndexedKeyCount_Includes_Primary_Folder_And_Aliases()
    {
        var matcher = new AuthorMatcher(new[]
        {
            Tracked("Arthur C. Clarke", folder: "Clarke, Arthur C.", aliases: new[] { "A. C. Clarke", "Arthur Charles Clarke" })
        });

        Assert.True(matcher.IndexedKeyCount >= 4);
    }

    [Fact]
    public void Resolve_Prefers_Metadata_Over_Filename_When_Both_Present()
    {
        var matcher = new AuthorMatcher(new[]
        {
            Tracked("Arthur C. Clarke", id: 1),
            Tracked("Isaac Asimov", id: 2),
        });

        var hit = matcher.Resolve("Isaac Asimov", null, @"X:\drop\Arthur C. Clarke - Rama.epub");

        Assert.NotNull(hit);
        Assert.Equal(2, hit!.Entry.TrackedAuthorId);
    }

    [Fact]
    public void Resolve_Uses_Forward_Filename_When_Metadata_Blank()
    {
        var matcher = new AuthorMatcher(new[] { Tracked("Isaac Asimov", id: 2) });

        var hit = matcher.Resolve("  ", null, @"X:\drop\Isaac Asimov - Foundation.epub");

        Assert.NotNull(hit);
        Assert.Equal(2, hit!.Entry.TrackedAuthorId);
    }

    [Fact]
    public void Resolve_Returns_Null_For_Reverse_Filename_With_Missing_Side()
    {
        var matcher = new AuthorMatcher(new[] { Tracked("Isaac Asimov", id: 2) });

        Assert.Null(matcher.Resolve(null, null, @"X:\drop\ - Isaac Asimov.epub"));
        Assert.Null(matcher.Resolve(null, null, @"X:\drop\Foundation - .epub"));
    }

    [Fact]
    public void ResolveFolderLayout_Returns_Null_Title_When_Author_Folder_Is_Immediate_Child()
    {
        var matcher = new AuthorMatcher(new[] { Tracked("Isaac Asimov", id: 2) });

        var (entry, title) = matcher.ResolveFolderLayout(@"X:\drop\Isaac Asimov", @"X:\drop");

        Assert.NotNull(entry);
        Assert.Null(title);
    }

    [Fact]
    public void ResolveFolderLayout_Returns_Nearest_Descendant_As_Title()
    {
        var matcher = new AuthorMatcher(new[] { Tracked("Isaac Asimov", id: 2) });

        var (entry, title) = matcher.ResolveFolderLayout(@"X:\drop\Isaac Asimov\Foundation\formats\epub", @"X:\drop");

        Assert.NotNull(entry);
        Assert.Equal("Foundation", title);
    }

    [Fact]
    public void ResolveFolderAncestor_Is_Case_Insensitive_For_Unknown_Folder_Name()
    {
        var matcher = new AuthorMatcher(new[] { Tracked("Isaac Asimov", id: 2) });

        var hit = matcher.ResolveFolderAncestor(@"X:\drop\__UNKNOWN\Isaac Asimov\Book", @"X:\drop");

        Assert.NotNull(hit);
        Assert.Equal(2, hit!.TrackedAuthorId);
    }

    [Fact]
    public void Blacklist_Removes_Entries_By_Folder_Name_Too()
    {
        var matcher = new AuthorMatcher(
            new[] { Tracked("Public Name", folder: "Hidden Alias", id: 8) },
            new[] { "hidden alias" });

        Assert.Null(matcher.TryGet("Public Name"));
        Assert.Null(matcher.TryGet("Hidden Alias"));
    }

    [Fact]
    public void AuthorKeyVariants_Yields_Nothing_For_Blank_After_Normalization()
    {
        Assert.Empty(AuthorMatcher.AuthorKeyVariants("!!! ???"));
    }

    [Fact]
    public void ExpandNameVariants_One_Token_Yields_Only_Original()
    {
        Assert.Equal(new[] { "asimov" }, AuthorMatcher.ExpandNameVariants("asimov").ToArray());
    }

    [Fact]
    public void TryGet_Resolves_Via_Folder_Name_Even_When_Display_Differs()
    {
        var matcher = new AuthorMatcher(new[]
        {
            Tracked("Canonical Display", folder: "Asimov, Isaac", id: 11)
        });

        var hit = matcher.TryGet("Isaac Asimov");

        Assert.NotNull(hit);
        Assert.Equal(11, hit!.TrackedAuthorId);
    }

    [Fact]
    public void Tracked_Primary_Name_Beats_Ol_Alias_Collision()
    {
        var matcher = new AuthorMatcher(new[]
        {
            Ol("Different Person", folder: "Different Person"),
            new AuthorIndexEntry("Different Person", "Different Person", false, AlternateNames: new[] { "Isaac Asimov" }),
            Tracked("Isaac Asimov", id: 5)
        });

        var hit = matcher.TryGet("Isaac Asimov");

        Assert.NotNull(hit);
        Assert.True(hit!.IsTracked);
        Assert.Equal(5, hit.TrackedAuthorId);
    }
}

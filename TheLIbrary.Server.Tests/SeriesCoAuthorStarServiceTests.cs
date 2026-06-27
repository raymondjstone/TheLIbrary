using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class SeriesCoAuthorStarServiceTests
{
    // authors: (Id, Name, Priority)   books: (AuthorId, SeriesId)

    [Fact]
    public void Stars_Unstarred_CoAuthor_Of_A_Starred_Authors_Series()
    {
        var ids = SeriesCoAuthorStarService.PickCoAuthorIdsToStar(
            authors: [(1, "Alpha", 3), (2, "Beta", 0)],   // 1 starred, 2 not, different names
            books: [(1, 100), (2, 100)]);                  // both write for series 100

        Assert.Equal([2], ids);
    }

    [Fact]
    public void Leaves_The_Starred_Author_Alone()
    {
        var ids = SeriesCoAuthorStarService.PickCoAuthorIdsToStar(
            authors: [(1, "Alpha", 3), (2, "Beta", 0)],
            books: [(1, 100), (2, 100)]);

        Assert.DoesNotContain(1, ids);
    }

    [Fact]
    public void Does_Not_Star_A_Same_Name_Duplicate_Record()
    {
        // Two "Terry Brooks" records sharing a series — the unstarred one is the same
        // person catalogued twice, not a real co-author, so it must NOT be starred.
        var ids = SeriesCoAuthorStarService.PickCoAuthorIdsToStar(
            authors: [(1, "Terry Brooks", 3), (2, "Terry Brooks", 0)],
            books: [(1, 100), (2, 100)]);

        Assert.Empty(ids);
    }

    [Fact]
    public void Ignores_Series_With_No_Starred_Author()
    {
        var ids = SeriesCoAuthorStarService.PickCoAuthorIdsToStar(
            authors: [(1, "Alpha", 0), (2, "Beta", 0)],
            books: [(1, 100), (2, 100)]);

        Assert.Empty(ids);
    }

    [Fact]
    public void Does_Not_Cross_Series()
    {
        var ids = SeriesCoAuthorStarService.PickCoAuthorIdsToStar(
            authors: [(1, "Alpha", 3), (2, "Beta", 0)],
            books: [(1, 100), (2, 200)]);                  // author 2 writes a DIFFERENT series

        Assert.Empty(ids);
    }

    [Fact]
    public void Already_Starred_CoAuthor_Is_Not_Returned()
    {
        var ids = SeriesCoAuthorStarService.PickCoAuthorIdsToStar(
            authors: [(1, "Alpha", 3), (2, "Beta", 2)],
            books: [(1, 100), (2, 100)]);

        Assert.Empty(ids);
    }

    [Fact]
    public void Standalone_Books_Without_A_Series_Are_Ignored()
    {
        var ids = SeriesCoAuthorStarService.PickCoAuthorIdsToStar(
            authors: [(1, "Alpha", 3), (2, "Beta", 0)],
            books: [(1, null), (2, null)]);

        Assert.Empty(ids);
    }
}

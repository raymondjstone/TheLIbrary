using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class SeriesCoAuthorStarServiceTests
{
    // authors: (Id, Priority)   books: (AuthorId, SeriesId)

    [Fact]
    public void Stars_Unstarred_CoAuthor_Of_A_Starred_Authors_Series()
    {
        var ids = SeriesCoAuthorStarService.PickCoAuthorIdsToStar(
            authors: [(1, 3), (2, 0)],          // 1 starred, 2 not
            books: [(1, 100), (2, 100)]);       // both write for series 100

        Assert.Equal([2], ids);
    }

    [Fact]
    public void Leaves_The_Starred_Author_Alone()
    {
        var ids = SeriesCoAuthorStarService.PickCoAuthorIdsToStar(
            authors: [(1, 3), (2, 0)],
            books: [(1, 100), (2, 100)]);

        Assert.DoesNotContain(1, ids);          // only co-authors, never the starred one
    }

    [Fact]
    public void Ignores_Series_With_No_Starred_Author()
    {
        var ids = SeriesCoAuthorStarService.PickCoAuthorIdsToStar(
            authors: [(1, 0), (2, 0)],
            books: [(1, 100), (2, 100)]);       // nobody starred → nothing to do

        Assert.Empty(ids);
    }

    [Fact]
    public void Does_Not_Cross_Series()
    {
        var ids = SeriesCoAuthorStarService.PickCoAuthorIdsToStar(
            authors: [(1, 3), (2, 0)],
            books: [(1, 100), (2, 200)]);       // author 2 writes a DIFFERENT series

        Assert.Empty(ids);
    }

    [Fact]
    public void Already_Starred_CoAuthor_Is_Not_Returned()
    {
        var ids = SeriesCoAuthorStarService.PickCoAuthorIdsToStar(
            authors: [(1, 3), (2, 2)],          // both already starred
            books: [(1, 100), (2, 100)]);

        Assert.Empty(ids);
    }

    [Fact]
    public void Standalone_Books_Without_A_Series_Are_Ignored()
    {
        var ids = SeriesCoAuthorStarService.PickCoAuthorIdsToStar(
            authors: [(1, 3), (2, 0)],
            books: [(1, null), (2, null)]);

        Assert.Empty(ids);
    }
}

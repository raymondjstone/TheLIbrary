using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class PhysicalAuthorStarServiceTests
{
    [Fact]
    public void PickAuthorIdsToStarForTests_Chooses_Unstarred_Authors_With_Physical_Books()
    {
        var ids = PhysicalAuthorStarService.PickAuthorIdsToStarForTests(
            [(1, 0), (2, 2), (3, 0), (4, 0)],
            [(1, true), (1, false), (2, true), (3, false)]);

        Assert.Equal([1], ids);
    }

    [Fact]
    public void PickAuthorIdsToStarForTests_Deduplicates_Multiple_Physical_Books()
    {
        var ids = PhysicalAuthorStarService.PickAuthorIdsToStarForTests(
            [(1, 0)],
            [(1, true), (1, true)]);

        Assert.Equal([1], ids);
    }
}

using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class AuthorRefreshCoordinatorTests
{
    [Fact]
    public void TryAcquire_Allows_First_Request_For_Author()
    {
        var gate = new AuthorRefreshCoordinator();

        Assert.True(gate.TryAcquire(42));
    }

    [Fact]
    public void TryAcquire_Rejects_Second_Request_For_Same_Author_Until_Released()
    {
        var gate = new AuthorRefreshCoordinator();

        Assert.True(gate.TryAcquire(42));
        Assert.False(gate.TryAcquire(42));

        gate.Release(42);

        Assert.True(gate.TryAcquire(42));
    }

    [Fact]
    public void TryAcquire_Allows_Different_Authors_Concurrently()
    {
        var gate = new AuthorRefreshCoordinator();

        Assert.True(gate.TryAcquire(1));
        Assert.True(gate.TryAcquire(2));
        Assert.True(gate.TryAcquire(999));
    }

    [Fact]
    public void Release_On_NonRunning_Author_Is_Harmless()
    {
        var gate = new AuthorRefreshCoordinator();

        gate.Release(123);

        Assert.True(gate.TryAcquire(123));
    }

    [Fact]
    public void Exception_Exposes_AuthorId_And_Message()
    {
        var ex = new AuthorRefreshAlreadyRunningException(7, "Terry Brooks");

        Assert.Equal(7, ex.AuthorId);
        Assert.Contains("Terry Brooks", ex.Message);
    }
}

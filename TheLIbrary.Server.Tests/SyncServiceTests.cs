using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Services.Scheduling;
using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class SyncServiceTests
{
    [Fact]
    public void TryStartSeed_Returns_Error_When_Coordinator_Is_Busy()
    {
        var coordinator = new BackgroundTaskCoordinator();
        Assert.True(coordinator.TryAcquire("other", out _));
        var sut = new SyncService(CreateScopeFactory(), coordinator, NullLogger<SyncService>.Instance);

        var started = sut.TryStartSeed(CancellationToken.None, out var error);

        Assert.False(started);
        Assert.Contains("Another task is already running", error);
    }

    [Fact]
    public void GetState_Returns_Clone()
    {
        var sut = new SyncService(CreateScopeFactory(), new BackgroundTaskCoordinator(), NullLogger<SyncService>.Instance);

        var state = sut.GetState();
        state.Message = "changed";

        Assert.NotEqual("changed", sut.GetState().Message);
    }

    private static IServiceScopeFactory CreateScopeFactory()
    {
        var services = new ServiceCollection();
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }
}

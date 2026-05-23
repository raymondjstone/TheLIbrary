using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Services.Incoming;
using TheLibrary.Server.Services.Scheduling;
using Xunit;

namespace TheLibrary.Server.Tests;

public class IncomingServiceTests
{
    [Fact]
    public void TryStart_Returns_Error_When_Coordinator_Is_Busy()
    {
        var coordinator = new BackgroundTaskCoordinator();
        Assert.True(coordinator.TryAcquire("other", out _));
        var sut = new IncomingService(CreateScopeFactory(), coordinator, NullLogger<IncomingService>.Instance);

        var started = sut.TryStart(CancellationToken.None, out var error);

        Assert.False(started);
        Assert.Contains("Another task is already running", error);
    }

    [Fact]
    public void GetState_Returns_Clone()
    {
        var sut = new IncomingService(CreateScopeFactory(), new BackgroundTaskCoordinator(), NullLogger<IncomingService>.Instance);

        var state = sut.GetState();
        state.Log.Add("mutated");

        Assert.Empty(sut.GetState().Log);
    }

    private static IServiceScopeFactory CreateScopeFactory()
    {
        var services = new ServiceCollection();
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }
}

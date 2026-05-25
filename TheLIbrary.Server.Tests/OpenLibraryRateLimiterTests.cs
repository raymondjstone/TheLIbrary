using System.Diagnostics;
using System.Reflection;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

public class OpenLibraryRateLimiterTests
{
    [Fact]
    public async Task RunAsync_Spaces_Request_Starts_Without_Waiting_For_Previous_Request_To_Finish()
    {
        var settings = new OpenLibrarySettings(new NoopScopeFactory());
        typeof(OpenLibrarySettings)
            .GetField("_contactEmail", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(settings, "me@example.org");

        var limiter = new OpenLibraryRateLimiter(settings);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = limiter.RunAsync(async () =>
        {
            firstStarted.SetResult();
            await releaseFirst.Task;
            return 1;
        }, CancellationToken.None);

        await firstStarted.Task;
        var sw = Stopwatch.StartNew();

        var second = limiter.RunAsync(() =>
        {
            secondStarted.SetResult(sw.Elapsed);
            return Task.FromResult(2);
        }, CancellationToken.None);

        var elapsed = await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.InRange(elapsed, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(900));
        Assert.False(first.IsCompleted);

        releaseFirst.SetResult();
        await Task.WhenAll(first, second);
    }
}

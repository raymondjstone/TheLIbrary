using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Services.Scheduling;
using Xunit;

namespace TheLibrary.Server.Tests;

public class ScheduledJobsTests
{
    [Fact]
    public async Task RunWithPolling_Returns_When_Schedule_Is_Disabled_And_Not_Manual()
    {
        var lifetime = new FakeHostLifetime();
        var jobs = new ScheduledJobs(null!, null!, null!, null!, null!, null!, null!, null!, null!, lifetime, NullLogger<ScheduledJobs>.Instance);
        var ran = false;

        await jobs.RunWithPollingForTests(
            new Dictionary<string, ScheduleEntry> { [ScheduleJobIds.Incoming] = new() { Cron = "0 1 * * *", Enabled = false } },
            ScheduleJobIds.Incoming,
            manualTrigger: false,
            _ => { ran = true; return (true, null); },
            () => false,
            TimeSpan.Zero,
            TimeSpan.Zero,
            CancellationToken.None);

        Assert.False(ran);
    }

    [Fact]
    public async Task RunWithPolling_Waits_Then_Starts_When_Service_Becomes_Available()
    {
        var lifetime = new FakeHostLifetime();
        var jobs = new ScheduledJobs(null!, null!, null!, null!, null!, null!, null!, null!, null!, lifetime, NullLogger<ScheduledJobs>.Instance);
        var attempts = 0;
        var running = true;

        await jobs.RunWithPollingForTests(
            new Dictionary<string, ScheduleEntry> { [ScheduleJobIds.Incoming] = new() { Cron = "0 1 * * *", Enabled = true } },
            ScheduleJobIds.Incoming,
            manualTrigger: false,
            _ =>
            {
                attempts++;
                return attempts == 1 ? (false, "busy") : (true, null);
            },
            () =>
            {
                var wasRunning = running;
                running = false;
                return wasRunning;
            },
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None);

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task RunWithPolling_Gives_Up_After_Wait_Ceiling()
    {
        var lifetime = new FakeHostLifetime();
        var jobs = new ScheduledJobs(null!, null!, null!, null!, null!, null!, null!, null!, null!, lifetime, NullLogger<ScheduledJobs>.Instance);
        var attempts = 0;

        await jobs.RunWithPollingForTests(
            new Dictionary<string, ScheduleEntry> { [ScheduleJobIds.Incoming] = new() { Cron = "0 1 * * *", Enabled = true } },
            ScheduleJobIds.Incoming,
            manualTrigger: false,
            _ =>
            {
                attempts++;
                return (false, "busy");
            },
            () => false,
            TimeSpan.FromMilliseconds(2),
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None);

        Assert.True(attempts >= 2);
    }

    [Fact]
    public async Task RunRefreshDueWorks_Skips_When_Hangfire_Queue_Is_Longer_Than_Three_Items()
    {
        ScheduledJobs.HangfireQueueLengthProviderOverride = () => 4;
        try
        {
            var lifetime = new FakeHostLifetime();
            var jobs = new ScheduledJobs(null!, null!, null!, null!, null!, null!, null!, null!, null!, lifetime, NullLogger<ScheduledJobs>.Instance);

            await jobs.RunRefreshDueWorks();
        }
        finally
        {
            ScheduledJobs.HangfireQueueLengthProviderOverride = null;
        }
    }

    private sealed class FakeHostLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}

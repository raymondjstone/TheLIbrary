using System.Text.Json;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Services.Scheduling;
using Xunit;

namespace TheLibrary.Server.Tests;

public class ScheduledJobsTests
{
    // Drives every public Run* entry point with the schedule for that job
    // explicitly disabled. RunWithPolling discards a non-manual run for a
    // disabled job before touching the underlying service, so each entry
    // point's body is exercised even though the service dependencies are null.
    [Fact]
    public async Task Every_Scheduled_Entry_Point_Returns_When_Its_Schedule_Is_Disabled()
    {
        // Avoid JobStorage.Current touching real Hangfire in RunRefreshDueWorks.
        ScheduledJobs.HangfireQueueLengthProviderOverride = () => 0;
        try
        {
            var dbName = $"scheduled-jobs-tests-{Guid.NewGuid():N}";
            var schedules = CreateScheduleServiceWithAllJobsDisabled(dbName);
            var jobs = new ScheduledJobs(
                null!, null!, null!, null!, null!, null!, null!, null!,
                null!, null!, null!, null!, schedules, new FakeHostLifetime(),
                NullLogger<ScheduledJobs>.Instance);

            var entryPoints = new Func<Task>[]
            {
                () => jobs.RunSync(),
                () => jobs.RunSeed(),
                () => jobs.RunAuthorUpdates(),
                () => jobs.RunIncoming(),
                () => jobs.RunReprocessUnknown(),
                () => jobs.RunRefreshDueWorks(),
                () => jobs.RunOrganizeSeries(),
                () => jobs.RunUnzip(),
                () => jobs.RunDisambiguateFolders(),
                () => jobs.RunSameNameAuthors(),
                () => jobs.RunStarPhysicalAuthors(),
                () => jobs.RunCacheOpenLibraryMetadata(),
                () => jobs.RunFlattenUnknown(),
                () => jobs.RunAdoptUnknownAuthors(),
                () => jobs.RunArchiveForeign(),
                () => jobs.RunMergeLinkedAuthors(),
            };

            foreach (var run in entryPoints)
                await run(); // must not throw despite null service dependencies

            Assert.Equal(ScheduleJobIds.All.Count, entryPoints.Length);
        }
        finally
        {
            ScheduledJobs.HangfireQueueLengthProviderOverride = null;
        }
    }

    private static ScheduleService CreateScheduleServiceWithAllJobsDisabled(string dbName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<LibraryDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();

        var cfg = new ScheduleConfig();
        foreach (var id in ScheduleJobIds.All)
            cfg.Jobs[id] = new ScheduleEntry { Cron = "0 1 * * *", Enabled = false };

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            db.AppSettings.Add(new AppSetting
            {
                Key = AppSettingKeys.Schedules,
                Value = JsonSerializer.Serialize(cfg, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            });
            db.SaveChanges();
        }

        return new ScheduleService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new NoopBackgroundJobClient(),
            new NoopRecurringJobManager(),
            NullLogger<ScheduleService>.Instance);
    }

    private sealed class NoopRecurringJobManager : IRecurringJobManager
    {
        public void RemoveIfExists(string recurringJobId) { }
        public void AddOrUpdate(string recurringJobId, Job job, string cronExpression, RecurringJobOptions options) { }
        public void Trigger(string recurringJobId) { }
    }

    private sealed class NoopBackgroundJobClient : IBackgroundJobClient
    {
        public bool ChangeState(string jobId, IState state, string expectedState) => true;
        public string Create(Job job, IState state) => "";
        public bool Delete(string jobId) => true;
        public bool Delete(string jobId, string fromState) => true;
        public bool Requeue(string jobId) => true;
        public bool Requeue(string jobId, string fromState) => true;
    }

    [Fact]
    public async Task RunWithPolling_Returns_When_Schedule_Is_Disabled_And_Not_Manual()
    {
        var lifetime = new FakeHostLifetime();
        var jobs = new ScheduledJobs(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, lifetime, NullLogger<ScheduledJobs>.Instance);
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
        var jobs = new ScheduledJobs(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, lifetime, NullLogger<ScheduledJobs>.Instance);
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
        var jobs = new ScheduledJobs(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, lifetime, NullLogger<ScheduledJobs>.Instance);
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
            var jobs = new ScheduledJobs(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, lifetime, NullLogger<ScheduledJobs>.Instance);

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

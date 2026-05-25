using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Scheduling;
using Xunit;

namespace TheLibrary.Server.Tests;

public class ScheduleServiceTests
{
    [Fact]
    public async Task GetAllAsync_Returns_Defaults_When_No_Config_Row_Exists()
    {
        var dbName = $"schedule-tests-{Guid.NewGuid():N}";
        await using var db = CreateDb(dbName);
        var sut = CreateService(new FakeRecurringJobManager(), dbName);

        var all = await sut.GetAllAsync();

        Assert.Equal(ScheduleJobIds.All.Count, all.Count);
        Assert.Equal(ScheduleJobIds.Defaults[ScheduleJobIds.Sync].Cron, all[ScheduleJobIds.Sync].Cron);
    }

    [Fact]
    public async Task UpdateAsync_Persists_Config_And_Registers_Job()
    {
        var recurring = new FakeRecurringJobManager();
        var dbName = $"schedule-tests-{Guid.NewGuid():N}";
        await using var db = CreateDb(dbName);
        var sut = CreateService(recurring, dbName);

        var entry = await sut.UpdateAsync(ScheduleJobIds.Sync, new ScheduleEntry { Cron = "0 8 * * *", Enabled = true });

        Assert.Equal("0 8 * * *", entry.Cron);
        Assert.Contains(ScheduleJobIds.Sync, recurring.AddedOrUpdatedJobIds);
        await using var verifyDb = CreateDb(dbName);
        Assert.Contains(await verifyDb.AppSettings.Select(x => x.Key + ":" + x.Value).ToListAsync(), s => s.StartsWith(AppSettingKeys.Schedules + ":", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdateAsync_Removes_Job_When_Disabled()
    {
        var dbName = $"schedule-tests-{Guid.NewGuid():N}";
        await using var db = CreateDb(dbName);
        var recurring = new FakeRecurringJobManager();
        var sut = CreateService(recurring, dbName);

        await sut.UpdateAsync(ScheduleJobIds.Incoming, new ScheduleEntry { Cron = "0 9 * * *", Enabled = false });

        Assert.Contains(ScheduleJobIds.Incoming, recurring.RemovedJobIds);
    }

    [Fact]
    public async Task UpdateAsync_Rejects_Unknown_Job()
    {
        var dbName = $"schedule-tests-{Guid.NewGuid():N}";
        await using var db = CreateDb(dbName);
        var sut = CreateService(new FakeRecurringJobManager(), dbName);

        await Assert.ThrowsAsync<ArgumentException>(() => sut.UpdateAsync("unknown", new ScheduleEntry { Cron = "0 1 * * *", Enabled = true }));
    }

    [Fact]
    public async Task GetAllAsync_Falls_Back_To_Defaults_When_Config_Is_Invalid_Json()
    {
        var dbName = $"schedule-tests-{Guid.NewGuid():N}";
        await using var db = CreateDb(dbName);
        db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.Schedules, Value = "{not-json" });
        await db.SaveChangesAsync();

        var sut = CreateService(new FakeRecurringJobManager(), dbName);
        var all = await sut.GetAllAsync();

        Assert.Equal(ScheduleJobIds.Defaults[ScheduleJobIds.Sync].Cron, all[ScheduleJobIds.Sync].Cron);
    }

    [Fact]
    public async Task ApplyAllAsync_Registers_Each_Configured_Job()
    {
        var dbName = $"schedule-tests-{Guid.NewGuid():N}";
        await using var db = CreateDb(dbName);
        db.AppSettings.Add(new AppSetting
        {
            Key = AppSettingKeys.Schedules,
            Value = "{\"jobs\":{\"sync\":{\"cron\":\"0 2 * * *\",\"enabled\":true},\"incoming\":{\"cron\":\"0 5 * * *\",\"enabled\":false}}}"
        });
        await db.SaveChangesAsync();

        var recurring = new FakeRecurringJobManager();
        var sut = CreateService(recurring, dbName);

        await sut.ApplyAllAsync();

        Assert.Contains(ScheduleJobIds.Sync, recurring.AddedOrUpdatedJobIds);
        Assert.Contains(ScheduleJobIds.Incoming, recurring.RemovedJobIds);
    }

    [Fact]
    public async Task GetAllAsync_Includes_StarPhysicalAuthors_Default()
    {
        var dbName = $"schedule-tests-{Guid.NewGuid():N}";
        await using var db = CreateDb(dbName);
        var sut = CreateService(new FakeRecurringJobManager(), dbName);

        var all = await sut.GetAllAsync();

        Assert.True(all.ContainsKey(ScheduleJobIds.StarPhysicalAuthors));
        Assert.Equal("0 10 * * *", all[ScheduleJobIds.StarPhysicalAuthors].Cron);
        Assert.True(all[ScheduleJobIds.StarPhysicalAuthors].Enabled);
    }

    [Fact]
    public async Task ClearFailedJobsAsync_Deletes_All_Failed_Jobs()
    {
        var dbName = $"schedule-tests-{Guid.NewGuid():N}";
        await using var db = CreateDb(dbName);
        var recurring = new FakeRecurringJobManager();
        var background = new FakeBackgroundJobClient();
        var storage = new FakeJobStorage(new[] { "job-1", "job-2", "job-3" }, background);

        try
        {
            JobStorage.Current = storage;
            var sut = CreateService(background, recurring, dbName);

            var deleted = await sut.ClearFailedJobsAsync();

            Assert.Equal(3, deleted);
            Assert.Equal(new[] { "job-1", "job-2", "job-3" }, background.DeletedJobIds);
        }
        finally
        {
            JobStorage.Current = null!;
        }
    }

    private static LibraryDbContext CreateDb(string? name = null)
    {
        var options = new DbContextOptionsBuilder<LibraryDbContext>()
            .UseInMemoryDatabase(name ?? $"schedule-tests-{Guid.NewGuid():N}")
            .Options;
        return new LibraryDbContext(options);
    }

    private static ScheduleService CreateService(IBackgroundJobClient backgroundJobs, IRecurringJobManager recurring, string dbName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<LibraryDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        services.AddSingleton(backgroundJobs);
        services.AddSingleton<IRecurringJobManager>(recurring);
        services.AddSingleton(NullLogger<ScheduleService>.Instance);
        var provider = services.BuildServiceProvider();
        return new ScheduleService(provider.GetRequiredService<IServiceScopeFactory>(), backgroundJobs, recurring, NullLogger<ScheduleService>.Instance);
    }

    private static ScheduleService CreateService(IRecurringJobManager recurring, string dbName)
        => CreateService(new FakeBackgroundJobClient(), recurring, dbName);

    private sealed class FakeRecurringJobManager : IRecurringJobManager
    {
        public List<string> AddedOrUpdatedJobIds { get; } = [];
        public List<string> RemovedJobIds { get; } = [];

        public void RemoveIfExists(string recurringJobId) => RemovedJobIds.Add(recurringJobId);
        public void AddOrUpdate(string recurringJobId, Job job, string cronExpression, RecurringJobOptions options) => AddedOrUpdatedJobIds.Add(recurringJobId);
        public void Trigger(string recurringJobId) => throw new NotSupportedException();
    }

    private sealed class FakeBackgroundJobClient : IBackgroundJobClient
    {
        public List<string> DeletedJobIds { get; } = [];

        public bool ChangeState(string jobId, IState state, string expectedState)
        {
            DeletedJobIds.Add(jobId);
            return true;
        }
        public string Create(Job job, IState state) => throw new NotSupportedException();
        public bool Delete(string jobId) => true;
        public bool Delete(string jobId, string fromState) => true;
        public bool Requeue(string jobId) => throw new NotSupportedException();
        public bool Requeue(string jobId, string fromState) => throw new NotSupportedException();
    }

    private sealed class FakeJobStorage(IReadOnlyCollection<string> failedJobIds, FakeBackgroundJobClient background) : JobStorage
    {
        public override IMonitoringApi GetMonitoringApi() => new FakeMonitoringApi(failedJobIds, background);
        public override IStorageConnection GetConnection() => throw new NotSupportedException();
    }

    private sealed class FakeMonitoringApi(IReadOnlyCollection<string> failedJobIds, FakeBackgroundJobClient background) : IMonitoringApi
    {
        public IList<QueueWithTopEnqueuedJobsDto> Queues() => new List<QueueWithTopEnqueuedJobsDto>();
        public long EnqueuedCount(string queue) => 0;
        public long FetchedCount(string queue) => 0;
        public long ScheduledCount() => 0;
        public long ProcessingCount() => 0;
        public long DeletedCount() => 0;
        public long FailedCount() => Math.Max(0, failedJobIds.Count - background.DeletedJobIds.Count);
        public long SucceededCount() => 0;
        public long SucceededListCount() => 0;
        public long DeletedListCount() => 0;
        public long AwaitingCount() => 0;
        public long ServersCount() => 0;
        public IDictionary<DateTime, long> HourlySucceededJobs() => new Dictionary<DateTime, long>();
        public IDictionary<DateTime, long> HourlyFailedJobs() => new Dictionary<DateTime, long>();
        public IDictionary<DateTime, long> SucceededByDatesCount() => new Dictionary<DateTime, long>();
        public IDictionary<DateTime, long> FailedByDatesCount() => new Dictionary<DateTime, long>();
        public JobDetailsDto? JobDetails(string jobId) => null;
        public StatisticsDto GetStatistics() => new();
        public IList<ServerDto> Servers() => new List<ServerDto>();
        public JobList<EnqueuedJobDto> EnqueuedJobs(string queue, int from, int perPage) => new(new List<KeyValuePair<string, EnqueuedJobDto>>());
        public JobList<FetchedJobDto> FetchedJobs(string queue, int from, int perPage) => new(new List<KeyValuePair<string, FetchedJobDto>>());
        public JobList<ProcessingJobDto> ProcessingJobs(int from, int count) => new(new List<KeyValuePair<string, ProcessingJobDto>>());
        public JobList<ScheduledJobDto> ScheduledJobs(int from, int count) => new(new List<KeyValuePair<string, ScheduledJobDto>>());
        public JobList<SucceededJobDto> SucceededJobs(int from, int count) => new(new List<KeyValuePair<string, SucceededJobDto>>());
        public JobList<FailedJobDto> FailedJobs(int from, int count)
            => new(failedJobIds
                .Except(background.DeletedJobIds)
                .Skip(from)
                .Take(count)
                .Select(id => new KeyValuePair<string, FailedJobDto>(id, new FailedJobDto()))
                .ToList());
        public JobList<DeletedJobDto> DeletedJobs(int from, int count) => new(new List<KeyValuePair<string, DeletedJobDto>>());
        public JobList<AwaitingJobDto> AwaitingJobs(int from, int count) => new(new List<KeyValuePair<string, AwaitingJobDto>>());
    }
}

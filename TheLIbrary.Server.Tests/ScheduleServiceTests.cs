using Hangfire;
using Hangfire.Common;
using Hangfire.Storage;
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

    private static LibraryDbContext CreateDb(string? name = null)
    {
        var options = new DbContextOptionsBuilder<LibraryDbContext>()
            .UseInMemoryDatabase(name ?? $"schedule-tests-{Guid.NewGuid():N}")
            .Options;
        return new LibraryDbContext(options);
    }

    private static ScheduleService CreateService(IRecurringJobManager recurring, string dbName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<LibraryDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        services.AddSingleton<IRecurringJobManager>(recurring);
        services.AddSingleton(NullLogger<ScheduleService>.Instance);
        var provider = services.BuildServiceProvider();
        return new ScheduleService(provider.GetRequiredService<IServiceScopeFactory>(), recurring, NullLogger<ScheduleService>.Instance);
    }

    private sealed class FakeRecurringJobManager : IRecurringJobManager
    {
        public List<string> AddedOrUpdatedJobIds { get; } = [];
        public List<string> RemovedJobIds { get; } = [];

        public void RemoveIfExists(string recurringJobId) => RemovedJobIds.Add(recurringJobId);
        public void AddOrUpdate(string recurringJobId, Job job, string cronExpression, RecurringJobOptions options) => AddedOrUpdatedJobIds.Add(recurringJobId);
        public void Trigger(string recurringJobId) => throw new NotSupportedException();
    }
}

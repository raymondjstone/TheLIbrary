namespace TheLibrary.Server.Services.Scheduling;

// Shape of the JSON blob persisted in AppSettings["Schedules"]. Missing jobs
// fall back to Defaults below. Stored as simple POCO classes (not records)
// so the built-in System.Text.Json setup round-trips them without needing
// converters for init-only properties.
public sealed class ScheduleConfig
{
    public Dictionary<string, ScheduleEntry> Jobs { get; set; } = new();
}

public sealed class ScheduleEntry
{
    public string Cron { get; set; } = "";
    public bool Enabled { get; set; }
}

public static class ScheduleJobIds
{
    public const string Sync = "sync";
    public const string Seed = "seed";
    public const string AuthorUpdates = "author-updates";
    public const string Incoming = "incoming";
    public const string ReprocessUnknown = "reprocess-unknown";

    public static readonly IReadOnlyList<string> All = new[] { Sync, Seed, AuthorUpdates, Incoming, ReprocessUnknown };

    // Default crons are staggered across the small hours so if every job is
    // flipped on without editing the time, they don't all queue at 02:00.
    // Disabled by default — user must explicitly opt in per job.
    public static readonly IReadOnlyDictionary<string, ScheduleEntry> Defaults =
        new Dictionary<string, ScheduleEntry>
        {
            [Sync] = new() { Cron = "0 2 * * *", Enabled = false },
            [Seed] = new() { Cron = "0 3 * * *", Enabled = false },
            [AuthorUpdates] = new() { Cron = "0 4 * * *", Enabled = false },
            [Incoming] = new() { Cron = "0 5 * * *", Enabled = false },
            [ReprocessUnknown] = new() { Cron = "0 6 * * *", Enabled = false },
        };
}

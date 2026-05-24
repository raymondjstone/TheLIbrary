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
    public const string RefreshWorks = "refresh-works";
    public const string OrganizeSeries = "organize-series";
    public const string Unzip = "unzip";
    public const string DisambiguateFolders = "disambiguate-folders";
    public const string SameNameAuthors = "same-name-authors";
    public const string StarPhysicalAuthors = "star-physical-authors";
    public const string CacheOpenLibraryMetadata = "cache-openlibrary-metadata";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Sync, Seed, AuthorUpdates, Incoming, ReprocessUnknown, RefreshWorks,
        OrganizeSeries, Unzip, DisambiguateFolders, SameNameAuthors,
        StarPhysicalAuthors, CacheOpenLibraryMetadata
    };

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
            [RefreshWorks] = new() { Cron = "0 7 * * *", Enabled = false },
            [OrganizeSeries] = new() { Cron = "0 1,13 * * *", Enabled = true },
            [Unzip] = new() { Cron = "0 0 * * *", Enabled = true },
            [DisambiguateFolders] = new() { Cron = "0 11 * * *", Enabled = true },
            // Every 6 hours (00:00, 06:00, 12:00, 18:00) — four times a day.
            [SameNameAuthors] = new() { Cron = "0 */6 * * *", Enabled = true },
            [StarPhysicalAuthors] = new() { Cron = "0 10 * * *", Enabled = true },
            [CacheOpenLibraryMetadata] = new() { Cron = "30 10 * * *", Enabled = true },
        };
}

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
    public const string FlattenUnknown = "flatten-unknown";
    public const string DedupeUnknown = "dedupe-unknown";
    public const string DedupeAuthorFiles = "dedupe-author-files";
    public const string PromoteManualBooks = "promote-manual-books";
    public const string AdoptUnknownAuthors = "adopt-unknown-authors";
    public const string ArchiveForeign = "archive-foreign";
    public const string MergeLinkedAuthors = "merge-linked-authors";
    public const string CheckIntegrity = "check-integrity";
    public const string PruneStaleFiles = "prune-stale-files";
    public const string ContentScan = "content-scan";
    public const string AssignAuthors = "assign-authors";
    public const string IndexFullText = "index-fulltext";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Sync, Seed, AuthorUpdates, Incoming, ReprocessUnknown, RefreshWorks,
        OrganizeSeries, Unzip, DisambiguateFolders, SameNameAuthors,
        StarPhysicalAuthors, CacheOpenLibraryMetadata, FlattenUnknown,
        DedupeUnknown, DedupeAuthorFiles, PromoteManualBooks, AdoptUnknownAuthors,
        ArchiveForeign, MergeLinkedAuthors, CheckIntegrity, PruneStaleFiles,
        ContentScan, AssignAuthors, IndexFullText
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
            [FlattenUnknown] = new() { Cron = "0 9 * * *", Enabled = false },
            // Delete byte-identical duplicate files inside the quarantine,
            // keeping one copy per content hash. Destructive, so it ships
            // disabled — the user opts in on the Schedules page.
            [DedupeUnknown] = new() { Cron = "30 9 * * *", Enabled = false },
            // Delete byte-identical duplicate files WITHIN each author folder
            // (one keeper), for authors with unmatched files. Daily at a quiet
            // hour; same duplicate determination as the __unknown dedupe.
            [DedupeAuthorFiles] = new() { Cron = "0 4 * * *", Enabled = true },
            // Search OL for each manually-catalogued book and promote/merge it
            // onto the real work once OL lists it. Capped per run (OL searches),
            // daily at a quiet half-hour. On by default — it only ever upgrades
            // manual rows in place, preserving series/files/ownership.
            [PromoteManualBooks] = new() { Cron = "30 7 * * *", Enabled = true },
            [AdoptUnknownAuthors] = new() { Cron = "0 8 * * *", Enabled = true },
            // Archive files of confirmed-foreign titles into the dedupe archive
            // folder — once a day at 23:00, enabled by default.
            [ArchiveForeign] = new() { Cron = "0 23 * * *", Enabled = true },
            // Fully merge user-linked duplicate authors into their canonical.
            // Only touches explicit links, so safe to run daily.
            [MergeLinkedAuthors] = new() { Cron = "0 5 * * *", Enabled = true },
            // Open/convert each ebook file and verify it has enough pages.
            // Heavy (PDF parse / Calibre conversion) and capped per run, so it
            // ships disabled — the user opts in on the Schedules page.
            [CheckIntegrity] = new() { Cron = "0 12 * * *", Enabled = false },
            // Prune leftover folder-pointer LocalBookFile rows. Cheap and
            // NAS-guarded, so on by default at a quiet hour.
            [PruneStaleFiles] = new() { Cron = "0 20 * * *", Enabled = true },
            // Read the front matter of unmatched / untracked files to guess their
            // author, title and series. Heavy (opens each file), so capped per
            // run and disabled by default — opt in on the Schedules page.
            [ContentScan] = new() { Cron = "0 21 * * *", Enabled = false },
            // File untracked content-scan rows under their author (created from
            // OpenLibrary if needed) — the Identified page's "Add all authors
            // from OL" bulk action. Capped per run (OL rate limits), so it runs
            // every 15 minutes to work through the backlog.
            [AssignAuthors] = new() { Cron = "*/15 * * * *", Enabled = true },
            // Extract and index ebook text for full-text search, capped per run by
            // FullTextIndexMaxPerRun. Heavy and opt-in (does nothing unless the
            // feature is enabled in Settings), so it ships disabled; default hourly.
            [IndexFullText] = new() { Cron = "0 * * * *", Enabled = false },
        };
}

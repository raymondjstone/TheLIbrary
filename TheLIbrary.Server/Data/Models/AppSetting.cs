using System.ComponentModel.DataAnnotations;

namespace TheLibrary.Server.Data.Models;

// Single-row-per-key configuration table. Used for small one-off settings
// that don't justify their own table (incoming folder, etc).
public class AppSetting
{
    public int Id { get; set; }

    [MaxLength(100)]
    public string Key { get; set; } = "";

    [MaxLength(2048)]
    public string Value { get; set; } = "";
}

public static class AppSettingKeys
{
    public const string IncomingFolder = "IncomingFolder";

    // ISO yyyy-MM-dd cursor for the author-updates sync task. Stored as the
    // last date whose merge-authors feed was successfully processed; the
    // next run starts from this same date (re-processes it) before advancing.
    public const string AuthorUpdateLastDate = "AuthorUpdateLastDate";

    // JSON blob of { jobId: { cron, enabled } } for the four recurring tasks.
    // Missing entries fall back to built-in defaults (disabled, daily).
    public const string Schedules = "Schedules";

    // OpenLibrary User-Agent identity, set from the Settings page. The app name
    // and contact email are sent on every OpenLibrary API call; a contact email
    // also unlocks OpenLibrary's 3 req/sec identified tier.
    public const string OpenLibraryAppName = "OpenLibraryAppName";
    public const string OpenLibraryContactEmail = "OpenLibraryContactEmail";

    // Limits for the refresh-due-works job, set from the Settings page.
    //   RefreshMaxAuthorsPerRun — most authors refreshed in one run (0 = no limit).
    //   RefreshEarlyWhenNoneDue — when no author is due, how many of the
    //                             soonest-due authors to refresh early.
    public const string RefreshMaxAuthorsPerRun = "RefreshMaxAuthorsPerRun";
    public const string RefreshEarlyWhenNoneDue = "RefreshEarlyWhenNoneDue";
    // Maximum number of days ahead of their NextFetchAt an author may be taken
    // early. 0 = no limit (take any author regardless of how far out they are).
    public const string RefreshEarlyMaxDaysAhead = "RefreshEarlyMaxDaysAhead";

    // Default per-author works-refresh cadence buckets, used when an author
    // does not have RefreshIntervalDays set explicitly.
    public const string RefreshCadenceRecentDays = "RefreshCadenceRecentDays";
    public const string RefreshCadenceMidDays = "RefreshCadenceMidDays";
    public const string RefreshCadenceDormantDays = "RefreshCadenceDormantDays";
    public const string RefreshCadenceOldOrEmptyDays = "RefreshCadenceOldOrEmptyDays";

    // Semicolon-separated format preference order for duplicate-file triage.
    // Earlier entries are recommended over later ones on the duplicates page.
    public const string DuplicateFormatPreference = "DuplicateFormatPreference";

    // Optional absolute path overriding the default `<library-location>/__unknown`
    // quarantine layout. When set, ALL unmatched / quarantined author folders
    // live under this single path instead of per-library-location subfolders.
    // Unset = use the default per-location layout.
    public const string UnknownFolder = "UnknownFolder";

    // Pushover (https://pushover.net) credentials for new-book alerts. Both
    // must be present for notifications to fire; unset = feature disabled.
    public const string PushoverAppToken = "PushoverAppToken";
    public const string PushoverUserKey = "PushoverUserKey";

    // Folder name (relative to each library root) used when archiving duplicate
    // files from the Duplicates page. Defaults to "__archive" when not set.
    public const string DedupeArchiveFolder = "DedupeArchiveFolder";
}

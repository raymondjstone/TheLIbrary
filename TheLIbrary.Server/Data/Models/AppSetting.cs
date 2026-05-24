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

    // Default per-author works-refresh cadence buckets, used when an author
    // does not have RefreshIntervalDays set explicitly.
    public const string RefreshCadenceRecentDays = "RefreshCadenceRecentDays";
    public const string RefreshCadenceMidDays = "RefreshCadenceMidDays";
    public const string RefreshCadenceDormantDays = "RefreshCadenceDormantDays";
    public const string RefreshCadenceOldOrEmptyDays = "RefreshCadenceOldOrEmptyDays";
}

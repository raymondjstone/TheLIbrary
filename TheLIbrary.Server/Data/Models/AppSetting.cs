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

    // When "true", hovering any cover thumbnail shows a large pop-up preview of
    // the cover. UI-only preference; unset/anything-but-"true" = disabled.
    public const string CoverHoverEnabled = "CoverHoverEnabled";

    // Size multiplier for the cover-hover pop-up. 1 = default, 2 = double all
    // dimensions, etc. Stored invariant-culture; unset/invalid = 1.
    public const string CoverHoverScale = "CoverHoverScale";

    // Absolute, writable directory where OpenLibrary cover images are cached and
    // served from (/cached-covers/...). Unset = derived from the library location
    // (its parent + "cached-covers"), so it lands on the same writable mount.
    public const string CachedCoversFolder = "CachedCoversFolder";

    // How many books the "Cache OL metadata" job processes per run. Unset = 1000.
    public const string CacheMetadataBatchSize = "CacheMetadataBatchSize";

    // How many files the "Check book integrity" job opens/converts per run.
    // The check is heavy (PDF parse / Calibre conversion) so it runs in capped
    // batches; already-checked files are skipped until their size changes.
    // Unset = BookIntegrityService.DefaultMaxBooksPerRun.
    public const string IntegrityMaxBooksPerRun = "IntegrityMaxBooksPerRun";

    // Semicolon-separated formats that count as an acceptable healthy
    // replacement on the Damaged page's "archive damaged that have a good
    // copy" action. Unset = "epub;mobi;lit".
    public const string IntegrityReplacementFormats = "IntegrityReplacementFormats";

    // How many files the "Identify books from content" job reads per run. The
    // front-matter extraction is heavier than a metadata read, so it's capped.
    // Unset = ContentScanService.DefaultMaxPerRun.
    public const string ContentScanMaxPerRun = "ContentScanMaxPerRun";

    // When "true", the "Identify books from content" job processes untracked
    // __unknown files before author-linked unmatched files (reverses the default
    // tier order). Unset / "false" = matched/unmatched author files first.
    public const string ContentScanUntrackedFirst = "ContentScanUntrackedFirst";
}

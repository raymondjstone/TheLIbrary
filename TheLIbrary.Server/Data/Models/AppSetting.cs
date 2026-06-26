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

    // When "true", the optional full-text search feature is on: the indexer
    // extracts ebook text into BookTextIndex and the Search page queries it.
    // Default OFF (unset/anything-but-"true") — indexing is heavy, so it's
    // strictly opt-in from the Settings page.
    public const string FullTextSearchEnabled = "FullTextSearchEnabled";

    // Max books the full-text indexer processes per run (scheduled or manual).
    // Unset/invalid = 200.
    public const string FullTextIndexMaxPerRun = "FullTextIndexMaxPerRun";

    // Per-run caps for the other capped jobs, editable on the Settings page
    // ("Background job run limits"). Each unset/invalid falls back to the service's
    // own default const, so behaviour is unchanged until the user overrides it.
    public const string PromoteManualBooksMaxPerRun = "PromoteManualBooksMaxPerRun"; // OL searches/run
    public const string ResolveWorksMaxPerRun = "ResolveWorksMaxPerRun";             // OL lookups/run
    public const string AssignAuthorsMaxPerRun = "AssignAuthorsMaxPerRun";           // OL lookups/run
    public const string AutoReplaceDamagedMaxPerRun = "AutoReplaceDamagedMaxPerRun"; // indexer grabs/run
    public const string PruneAuthorsMaxPerRun = "PruneAuthorsMaxPerRun";             // deletions/run

    // Extend full-text indexing beyond matched books. Both default OFF ("true"
    // to enable). UnmatchedAuthorFiles = files in an author folder not linked to
    // a Book; UnknownFiles = loose files in the __unknown quarantine.
    public const string FullTextIndexUnmatchedAuthorFiles = "FullTextIndexUnmatchedAuthorFiles";
    public const string FullTextIndexUnknownFiles = "FullTextIndexUnknownFiles";

    // Download automation (optional). A Newznab indexer (URL + API key) is
    // searched for a wanted book; the best NZB is handed to a SABnzbd instance
    // (URL + API key, optional category). Both keys are entered on the Settings
    // page; unset = feature disabled and the Grab button is hidden.
    public const string NewznabUrl = "NewznabUrl";
    public const string NewznabApiKey = "NewznabApiKey";
    public const string SabnzbdUrl = "SabnzbdUrl";
    public const string SabnzbdApiKey = "SabnzbdApiKey";
    public const string SabnzbdCategory = "SabnzbdCategory";

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

    // Maximum number of results returned by user-facing OpenLibrary title and
    // author searches (search-works / search-authors endpoints). Does not affect
    // internal callers such as ISBN lookup or folder-suggestion queries.
    // Unset = 20.
    public const string OlSearchResultsLimit = "OlSearchResultsLimit";

    // When "true", the "Identify books from content" job processes untracked
    // __unknown files before author-linked unmatched files (reverses the default
    // tier order). Unset / "false" = matched/unmatched author files first.
    public const string ContentScanUntrackedFirst = "ContentScanUntrackedFirst";

    // LLM-assisted identification (optional, paid). An LLM reads the signals we
    // already have (filename, embedded metadata, ISBN, a front-matter snippet) and
    // returns title/author for files the deterministic paths couldn't resolve.
    // Off unless enabled AND an API key is set. The guess still goes through
    // OpenLibrary validation, so a hallucinated author is rejected.
    public const string LlmEnabled = "LlmEnabled";
    public const string LlmProvider = "LlmProvider";      // "anthropic" (Claude) | "openai" (ChatGPT); unset = anthropic
    public const string LlmApiKey = "LlmApiKey";          // write-only from the UI; the active provider's key
    public const string LlmModel = "LlmModel";            // unset = the provider's default model
    public const string LlmBaseUrl = "LlmBaseUrl";        // unset = the provider's default endpoint (override for proxies/gateways)
    public const string LlmMaxPerRun = "LlmMaxPerRun";    // cap per job run
    public const string LlmMaxPerDay = "LlmMaxPerDay";    // hard daily cap to bound cost
    // Rolling daily-usage counter (date + count) used to enforce LlmMaxPerDay.
    public const string LlmUsageDate = "LlmUsageDate";
    public const string LlmUsageCount = "LlmUsageCount";

    // Optional provider ADMIN/org keys (separate from the message key above) used
    // only to read org-level SPEND for the Health page. The providers don't expose
    // a remaining-credit figure via the API, so this reports $ spent in a window,
    // not balance. Beta endpoints; write-only from the UI.
    public const string LlmOpenAiAdminKey = "LlmOpenAiAdminKey";
    public const string LlmAnthropicAdminKey = "LlmAnthropicAdminKey";
}

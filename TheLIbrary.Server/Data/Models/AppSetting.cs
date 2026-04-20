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
}

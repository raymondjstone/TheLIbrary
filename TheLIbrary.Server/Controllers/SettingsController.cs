using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.OpenLibrary;

namespace TheLibrary.Server.Controllers;

[ApiController]
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    private readonly LibraryDbContext _db;
    public SettingsController(LibraryDbContext db) { _db = db; }

    public sealed record IncomingDto(string? Path, bool Exists);
    public sealed record UpdateIncoming(string? Path);

    [HttpGet("incoming")]
    public async Task<IncomingDto> GetIncoming(CancellationToken ct)
    {
        var s = await _db.AppSettings
            .FirstOrDefaultAsync(x => x.Key == AppSettingKeys.IncomingFolder, ct);
        var path = string.IsNullOrWhiteSpace(s?.Value) ? null : s!.Value;
        return new IncomingDto(path, path is not null && Directory.Exists(path));
    }

    [HttpPut("incoming")]
    public async Task<IncomingDto> SetIncoming([FromBody] UpdateIncoming body, CancellationToken ct)
    {
        var path = body.Path?.Trim() ?? "";
        var s = await _db.AppSettings
            .FirstOrDefaultAsync(x => x.Key == AppSettingKeys.IncomingFolder, ct);
        if (s is null)
        {
            _db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.IncomingFolder, Value = path });
        }
        else s.Value = path;
        await _db.SaveChangesAsync(ct);
        return new IncomingDto(string.IsNullOrWhiteSpace(path) ? null : path,
            !string.IsNullOrWhiteSpace(path) && Directory.Exists(path));
    }

    public sealed record OpenLibraryIdentityDto(
        string AppName, string ContactEmail, bool Identified, string UserAgent);
    public sealed record UpdateOpenLibraryIdentity(string? AppName, string? ContactEmail);

    // The OpenLibrary User-Agent identity (app name + contact email). Stored in
    // the database so it stays out of the git repo and is set per deployment.
    [HttpGet("openlibrary")]
    public ActionResult<OpenLibraryIdentityDto> GetOpenLibrary([FromServices] OpenLibrarySettings ol)
        => new OpenLibraryIdentityDto(ol.AppName, ol.ContactEmail, ol.IsIdentified, ol.UserAgent);

    [HttpPut("openlibrary")]
    public async Task<ActionResult<OpenLibraryIdentityDto>> SetOpenLibrary(
        [FromBody] UpdateOpenLibraryIdentity body,
        [FromServices] OpenLibrarySettings ol,
        CancellationToken ct)
    {
        var email = body.ContactEmail?.Trim() ?? "";
        if (email.Length > 0 && (!email.Contains('@') || email.Contains(' ')))
            return BadRequest(new { error = "Contact email doesn't look like an email address." });

        await ol.UpdateAsync(body.AppName, email, ct);
        return new OpenLibraryIdentityDto(ol.AppName, ol.ContactEmail, ol.IsIdentified, ol.UserAgent);
    }

    public sealed record RefreshLimitsDto(int MaxAuthorsPerRun, int MaxEarlyWhenNoneDue);
    public sealed record RefreshCadenceDto(int RecentDays, int MidDays, int DormantDays, int OldOrEmptyDays);
    public sealed record DuplicateFormatPreferenceDto(string[] Formats);

    // Limits for the refresh-due-works job: how many authors it refreshes per
    // run (0 = no limit), and how many to refresh early when none are due.
    [HttpGet("refresh-limits")]
    public async Task<RefreshLimitsDto> GetRefreshLimits(CancellationToken ct)
    {
        var rows = await _db.AppSettings
            .Where(s => s.Key == AppSettingKeys.RefreshMaxAuthorsPerRun
                     || s.Key == AppSettingKeys.RefreshEarlyWhenNoneDue)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        return new RefreshLimitsDto(
            ReadInt(rows, AppSettingKeys.RefreshMaxAuthorsPerRun, 0),
            ReadInt(rows, AppSettingKeys.RefreshEarlyWhenNoneDue, 200));
    }

    [HttpPut("refresh-limits")]
    public async Task<ActionResult<RefreshLimitsDto>> SetRefreshLimits(
        [FromBody] RefreshLimitsDto body, CancellationToken ct)
    {
        if (body.MaxAuthorsPerRun < 0 || body.MaxEarlyWhenNoneDue < 0)
            return BadRequest(new { error = "Values cannot be negative." });

        await UpsertSettingAsync(AppSettingKeys.RefreshMaxAuthorsPerRun,
            body.MaxAuthorsPerRun.ToString(), ct);
        await UpsertSettingAsync(AppSettingKeys.RefreshEarlyWhenNoneDue,
            body.MaxEarlyWhenNoneDue.ToString(), ct);
        await _db.SaveChangesAsync(ct);
        return new RefreshLimitsDto(body.MaxAuthorsPerRun, body.MaxEarlyWhenNoneDue);
    }

    [HttpGet("refresh-cadence")]
    public async Task<RefreshCadenceDto> GetRefreshCadence(CancellationToken ct)
    {
        var rows = await _db.AppSettings
            .Where(s => s.Key == AppSettingKeys.RefreshCadenceRecentDays
                     || s.Key == AppSettingKeys.RefreshCadenceMidDays
                     || s.Key == AppSettingKeys.RefreshCadenceDormantDays
                     || s.Key == AppSettingKeys.RefreshCadenceOldOrEmptyDays)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        var defaults = TheLibrary.Server.Services.Sync.AuthorRefresher.RefreshCadenceSettings.Defaults;
        return new RefreshCadenceDto(
            ReadInt(rows, AppSettingKeys.RefreshCadenceRecentDays, defaults.RecentDays),
            ReadInt(rows, AppSettingKeys.RefreshCadenceMidDays, defaults.MidDays),
            ReadInt(rows, AppSettingKeys.RefreshCadenceDormantDays, defaults.DormantDays),
            ReadInt(rows, AppSettingKeys.RefreshCadenceOldOrEmptyDays, defaults.OldOrEmptyDays));
    }

    [HttpPut("refresh-cadence")]
    public async Task<ActionResult<RefreshCadenceDto>> SetRefreshCadence(
        [FromBody] RefreshCadenceDto body, CancellationToken ct)
    {
        if (body.RecentDays <= 0 || body.MidDays <= 0 || body.DormantDays <= 0 || body.OldOrEmptyDays <= 0)
            return BadRequest(new { error = "Cadence values must be greater than zero." });

        await UpsertSettingAsync(AppSettingKeys.RefreshCadenceRecentDays, body.RecentDays.ToString(), ct);
        await UpsertSettingAsync(AppSettingKeys.RefreshCadenceMidDays, body.MidDays.ToString(), ct);
        await UpsertSettingAsync(AppSettingKeys.RefreshCadenceDormantDays, body.DormantDays.ToString(), ct);
        await UpsertSettingAsync(AppSettingKeys.RefreshCadenceOldOrEmptyDays, body.OldOrEmptyDays.ToString(), ct);
        await _db.SaveChangesAsync(ct);

        return new RefreshCadenceDto(body.RecentDays, body.MidDays, body.DormantDays, body.OldOrEmptyDays);
    }

    [HttpGet("duplicate-format-preference")]
    public async Task<DuplicateFormatPreferenceDto> GetDuplicateFormatPreference(CancellationToken ct)
    {
        var row = await _db.AppSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == AppSettingKeys.DuplicateFormatPreference, ct);
        var formats = ParseFormats(row?.Value);
        return new DuplicateFormatPreferenceDto(formats.Length > 0 ? formats : BooksController.DefaultFormatPreference);
    }

    [HttpPut("duplicate-format-preference")]
    public async Task<ActionResult<DuplicateFormatPreferenceDto>> SetDuplicateFormatPreference(
        [FromBody] DuplicateFormatPreferenceDto body,
        CancellationToken ct)
    {
        var cleaned = (body.Formats ?? Array.Empty<string>())
            .Select(f => f?.Trim().TrimStart('.').ToLowerInvariant())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (cleaned.Length == 0)
            return BadRequest(new { error = "At least one format is required." });

        await UpsertSettingAsync(AppSettingKeys.DuplicateFormatPreference, string.Join(';', cleaned), ct);
        await _db.SaveChangesAsync(ct);
        return new DuplicateFormatPreferenceDto(cleaned);
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> rows, string key, int fallback)
        => rows.TryGetValue(key, out var v) && int.TryParse(v, out var n) && n >= 0 ? n : fallback;

    private static string[] ParseFormats(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(f => f.TrimStart('.').ToLowerInvariant())
                .Where(f => f.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private async Task UpsertSettingAsync(string key, string value, CancellationToken ct)
    {
        var row = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) _db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else row.Value = value;
    }
}

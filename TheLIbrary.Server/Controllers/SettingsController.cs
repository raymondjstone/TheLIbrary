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

    private static int ReadInt(IReadOnlyDictionary<string, string> rows, string key, int fallback)
        => rows.TryGetValue(key, out var v) && int.TryParse(v, out var n) && n >= 0 ? n : fallback;

    private async Task UpsertSettingAsync(string key, string value, CancellationToken ct)
    {
        var row = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) _db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else row.Value = value;
    }
}

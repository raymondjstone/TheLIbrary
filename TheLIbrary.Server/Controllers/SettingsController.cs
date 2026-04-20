using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;

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
}

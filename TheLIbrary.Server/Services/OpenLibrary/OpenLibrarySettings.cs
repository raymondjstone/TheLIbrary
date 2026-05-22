using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;

namespace TheLibrary.Server.Services.OpenLibrary;

// Holds the OpenLibrary User-Agent identity — an application name and a contact
// email — backed by AppSetting rows and editable from the Settings page.
//
// Kept as an in-memory singleton so the rate limiter and HTTP client can read
// it cheaply on every call. LoadAsync populates it at startup; UpdateAsync
// writes through to the database and refreshes the cache so an edit in the UI
// takes effect immediately, with no restart.
//
// Storing the identity in the database (not appsettings.json) keeps the
// contact email out of the git repo, and lets each deployment set its own —
// nobody should ever reuse another deployment's OpenLibrary identity.
public sealed class OpenLibrarySettings
{
    private readonly IServiceScopeFactory _scopeFactory;
    private volatile string _appName = "";
    private volatile string _contactEmail = "";

    public OpenLibrarySettings(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public string AppName => _appName;
    public string ContactEmail => _contactEmail;

    // Identified callers (a contact email is set) get OpenLibrary's 3 req/sec
    // tier; anonymous callers are held to 1 req/sec.
    public bool IsIdentified => _contactEmail.Length > 0;

    // The User-Agent header value sent on every OpenLibrary request.
    public string UserAgent
    {
        get
        {
            var name = _appName.Length > 0 ? _appName : "TheLibrary";
            return _contactEmail.Length > 0 ? $"{name} ({_contactEmail})" : name;
        }
    }

    // Loads the persisted values into memory. Called once at startup.
    public async Task LoadAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        _appName = await ReadAsync(db, AppSettingKeys.OpenLibraryAppName, ct);
        _contactEmail = await ReadAsync(db, AppSettingKeys.OpenLibraryContactEmail, ct);
    }

    // Persists new values and refreshes the in-memory cache.
    public async Task UpdateAsync(string? appName, string? contactEmail, CancellationToken ct = default)
    {
        var name = appName?.Trim() ?? "";
        var email = contactEmail?.Trim() ?? "";

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        await WriteAsync(db, AppSettingKeys.OpenLibraryAppName, name, ct);
        await WriteAsync(db, AppSettingKeys.OpenLibraryContactEmail, email, ct);
        await db.SaveChangesAsync(ct);

        _appName = name;
        _contactEmail = email;
    }

    private static async Task<string> ReadAsync(LibraryDbContext db, string key, CancellationToken ct)
    {
        var row = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        return row?.Value?.Trim() ?? "";
    }

    private static async Task WriteAsync(LibraryDbContext db, string key, string value, CancellationToken ct)
    {
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else row.Value = value;
    }
}

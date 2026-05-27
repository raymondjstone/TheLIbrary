using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;

namespace TheLibrary.Server.Services.Pushover;

public sealed record PushoverResult(bool Sent, string? Error);

// Thin wrapper around the Pushover REST endpoint. Reads the app token and
// user key from AppSettings each call so an in-app credential change takes
// effect without restarting. Returns Sent=false (with a reason) instead of
// throwing so the caller — AuthorRefresher — can log per-book outcomes and
// continue with the next book.
public sealed class PushoverClient
{
    private const string Endpoint = "https://api.pushover.net/1/messages.json";

    private readonly IHttpClientFactory _http;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PushoverClient> _log;

    public PushoverClient(
        IHttpClientFactory http,
        IServiceScopeFactory scopeFactory,
        ILogger<PushoverClient> log)
    {
        _http = http;
        _scopeFactory = scopeFactory;
        _log = log;
    }

    public async Task<(string? Token, string? User)> GetCredentialsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        return await LoadAsync(db, ct);
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken ct)
    {
        var (token, user) = await GetCredentialsAsync(ct);
        return !string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(user);
    }

    public Task<PushoverResult> SendAsync(string title, string message, string? url, CancellationToken ct)
        => SendAsync(null, title, message, url, ct);

    public async Task<PushoverResult> SendAsync(
        (string? Token, string? User)? overrideCreds,
        string title,
        string message,
        string? url,
        CancellationToken ct)
    {
        string? token, user;
        if (overrideCreds.HasValue)
            (token, user) = overrideCreds.Value;
        else
            (token, user) = await GetCredentialsAsync(ct);

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(user))
            return new PushoverResult(false, "Pushover credentials are not configured");

        var form = new Dictionary<string, string>
        {
            ["token"] = token,
            ["user"] = user,
            ["title"] = title,
            ["message"] = message,
        };
        if (!string.IsNullOrWhiteSpace(url)) form["url"] = url;

        try
        {
            using var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            using var response = await client.PostAsync(Endpoint, new FormUrlEncodedContent(form), ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _log.LogWarning(
                    "Pushover send failed: {Status} {Body}",
                    response.StatusCode, body);
                return new PushoverResult(false, $"{(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            }
            return new PushoverResult(true, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Pushover send threw");
            return new PushoverResult(false, ex.Message);
        }
    }

    private static async Task<(string? Token, string? User)> LoadAsync(LibraryDbContext db, CancellationToken ct)
    {
        var rows = await db.AppSettings
            .AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.PushoverAppToken
                     || s.Key == AppSettingKeys.PushoverUserKey)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        rows.TryGetValue(AppSettingKeys.PushoverAppToken, out var token);
        rows.TryGetValue(AppSettingKeys.PushoverUserKey, out var user);
        return (string.IsNullOrWhiteSpace(token) ? null : token.Trim(),
                string.IsNullOrWhiteSpace(user) ? null : user.Trim());
    }
}

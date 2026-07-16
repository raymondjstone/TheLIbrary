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
//
// Logs every notification sent to the Activity page for audit trail purposes.
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

        PushoverResult result;
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
                result = new PushoverResult(false, $"{(int)response.StatusCode} {response.ReasonPhrase}: {body}");
            }
            else
            {
                result = new PushoverResult(true, null);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Pushover send threw");
            result = new PushoverResult(false, ex.Message);
        }

        // Log every notification attempt to the Activity page
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

            var detail = result.Sent
                ? $"Notification sent: \"{title}\" — {message}"
                : $"Notification failed: \"{title}\" — {message} (Error: {result.Error})";

            ActivityLogger.Record(
                db,
                action: "pushover-notification",
                detail: detail,
                source: "pushover",
                bookId: null);

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Don't fail the notification if activity logging fails
            _log.LogWarning(ex, "Failed to log Pushover notification to activity page");
        }

        return result;
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

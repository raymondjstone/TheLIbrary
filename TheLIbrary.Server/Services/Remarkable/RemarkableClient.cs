using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;

namespace TheLibrary.Server.Services.Remarkable;

// Talks to reMarkable's cloud API. Two-step auth:
//   1. User gets an 8-char OTP from https://my.remarkable.com/device/desktop/connect
//   2. We exchange it for a long-lived DEVICE token (kept in the DB)
//   3. Each run exchanges the device token for a short-lived USER token (JWT)
// File uploads use the v3 "cloud 1.5" endpoint: a single PUT of the raw
// EPUB/PDF with a base64-encoded JSON metadata header.
public sealed class RemarkableClient
{
    private readonly LibraryDbContext _db;
    private readonly HttpClient _http;
    private readonly RemarkableOptions _opts;
    private readonly CalibreConverter _converter;
    private readonly ILogger<RemarkableClient> _log;

    // Refresh the user token this long before the JWT `exp` to avoid racing
    // the clock against a multi-MB upload.
    private static readonly TimeSpan UserTokenRefreshWindow = TimeSpan.FromMinutes(5);

    // Extensions Calibre can turn into a reMarkable-compatible EPUB. Order
    // is rough preference for fidelity (native ebook formats first, then
    // word-processing, then legacy/comic).
    private static readonly string[] ConvertibleExtensions =
    {
        ".mobi", ".azw3", ".azw", ".kf8", ".azw4", ".prc", ".pdb",
        ".fb2", ".fbz", ".docx", ".odt", ".lit", ".cbz"
    };

    public RemarkableClient(
        LibraryDbContext db,
        IHttpClientFactory httpFactory,
        IOptions<RemarkableOptions> opts,
        CalibreConverter converter,
        ILogger<RemarkableClient> log)
    {
        _db = db;
        _http = httpFactory.CreateClient("remarkable");
        _opts = opts.Value;
        _converter = converter;
        _log = log;
    }

    public async Task<RemarkableAuth?> GetAuthAsync(CancellationToken ct) =>
        await _db.RemarkableAuths.FirstOrDefaultAsync(ct);

    public async Task<RemarkableAuth> ConnectAsync(string otpCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(otpCode))
            throw new RemarkableException("One-time code is required.");
        var code = otpCode.Trim();

        var existing = await _db.RemarkableAuths.FirstOrDefaultAsync(ct);
        // Reuse the existing device GUID if the user is re-pairing — keeps
        // the reMarkable cloud dashboard from filling up with stale entries.
        var deviceId = existing?.DeviceId is { Length: > 0 } d ? d : Guid.NewGuid().ToString();

        var payload = JsonSerializer.Serialize(new
        {
            code,
            deviceDesc = _opts.DeviceDescription,
            deviceID = deviceId
        });

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{_opts.AuthHost.TrimEnd('/')}/token/json/2/device/new")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new RemarkableException(
                $"reMarkable rejected the one-time code ({(int)resp.StatusCode}). " +
                "Generate a fresh code at https://my.remarkable.com/device/desktop/connect and try again.");

        var deviceToken = body.Trim();
        if (string.IsNullOrWhiteSpace(deviceToken))
            throw new RemarkableException("reMarkable returned an empty device token.");

        if (existing is null)
        {
            existing = new RemarkableAuth
            {
                DeviceToken = deviceToken,
                DeviceId = deviceId,
                ConnectedAt = DateTime.UtcNow
            };
            _db.RemarkableAuths.Add(existing);
        }
        else
        {
            existing.DeviceToken = deviceToken;
            existing.DeviceId = deviceId;
            existing.ConnectedAt = DateTime.UtcNow;
            existing.CachedUserToken = null;
            existing.UserTokenExpiresAt = null;
        }
        await _db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        var rows = await _db.RemarkableAuths.ToListAsync(ct);
        _db.RemarkableAuths.RemoveRange(rows);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<string> SendFileAsync(LocalBookFile file, CancellationToken ct)
    {
        var auth = await _db.RemarkableAuths.FirstOrDefaultAsync(ct)
            ?? throw new RemarkableException("reMarkable is not connected. Pair a device on the Settings page first.");

        // LocalBookFile.FullPath points to the Calibre title folder, not to
        // an individual ebook file. Prefer a native EPUB/PDF; otherwise run
        // Calibre's ebook-convert on the best available source. Tracked so
        // the temp output gets deleted whether the upload succeeds or fails.
        var (sourcePath, tempFile) = await ResolveOrConvertAsync(file.FullPath, ct);

        try
        {
            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            var contentType = ext switch
            {
                ".epub" => "application/epub+zip",
                ".pdf"  => "application/pdf",
                _ => throw new RemarkableException($"Unexpected extension {ext} after resolve.")
            };

            var userToken = await GetOrRefreshUserTokenAsync(auth, ct);

            // Display name on the device: prefer the work title if we have it,
            // otherwise the Calibre title folder, else the ebook filename stem.
            var displayName = !string.IsNullOrWhiteSpace(file.Book?.Title)
                ? file.Book!.Title
                : !string.IsNullOrWhiteSpace(file.TitleFolder)
                    ? file.TitleFolder
                    : Path.GetFileNameWithoutExtension(sourcePath);

            // rm-meta is a base64-encoded JSON blob; current clients send
            // file_name (used as the display name). reMarkable ignores unknown
            // fields and computes page metadata itself.
            var metaJson = JsonSerializer.Serialize(new { file_name = displayName });
            var metaHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes(metaJson));

            await using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{_opts.ApiHost.TrimEnd('/')}/doc/v2/files")
            {
                Content = new StreamContent(stream)
            };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
            req.Headers.TryAddWithoutValidation("rm-meta", metaHeader);
            req.Headers.TryAddWithoutValidation("rm-source", "the-library");

            using var resp = await _http.SendAsync(req, ct);
            var respBody = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("reMarkable upload failed: {Status} {Body}", resp.StatusCode, respBody);
                throw new RemarkableException(
                    $"reMarkable upload failed ({(int)resp.StatusCode}). {TrimForMessage(respBody)}");
            }

            auth.LastSentAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return displayName;
        }
        finally
        {
            if (tempFile is not null)
            {
                try { System.IO.File.Delete(tempFile); } catch { /* best effort */ }
            }
        }
    }

    // Resolves the path to upload. Returns (pathToUpload, tempFileToDelete).
    // tempFileToDelete is non-null only when Calibre produced a temporary EPUB
    // that the caller is responsible for cleaning up.
    private async Task<(string path, string? tempFile)> ResolveOrConvertAsync(string folderOrFile, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folderOrFile))
            throw new RemarkableException("Local file has no path on disk.");

        // Edge case: FullPath already points at a file (older rows where a
        // loose file sat directly under the author folder).
        if (System.IO.File.Exists(folderOrFile))
        {
            var ext = Path.GetExtension(folderOrFile).ToLowerInvariant();
            if (ext is ".epub" or ".pdf") return (folderOrFile, null);
            var epub = await _converter.ConvertToEpubAsync(folderOrFile, ct);
            return (epub, epub);
        }

        if (!Directory.Exists(folderOrFile))
            throw new RemarkableException($"Folder no longer exists on disk: {folderOrFile}");

        List<string> files;
        try { files = Directory.EnumerateFiles(folderOrFile).ToList(); }
        catch (Exception ex)
        { throw new RemarkableException($"Could not read folder {folderOrFile}: {ex.Message}"); }

        // Native formats first — no conversion wins on fidelity and speed.
        var epubPath = files.FirstOrDefault(p => Path.GetExtension(p).Equals(".epub", StringComparison.OrdinalIgnoreCase));
        if (epubPath is not null) return (epubPath, null);

        var pdfPath = files.FirstOrDefault(p => Path.GetExtension(p).Equals(".pdf", StringComparison.OrdinalIgnoreCase));
        if (pdfPath is not null) return (pdfPath, null);

        var convertible = ConvertibleExtensions
            .SelectMany(ext => files.Where(p => Path.GetExtension(p).Equals(ext, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault();
        if (convertible is null)
            throw new RemarkableException($"No ebook files found in {folderOrFile}.");

        try
        {
            var converted = await _converter.ConvertToEpubAsync(convertible, ct);
            return (converted, converted);
        }
        catch (CalibreConversionException ex)
        {
            throw new RemarkableException(
                $"Calibre conversion of {Path.GetFileName(convertible)} failed: {ex.Message}", ex);
        }
    }

    private async Task<string> GetOrRefreshUserTokenAsync(RemarkableAuth auth, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(auth.CachedUserToken)
            && auth.UserTokenExpiresAt is { } expires
            && expires - DateTime.UtcNow > UserTokenRefreshWindow)
        {
            return auth.CachedUserToken!;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{_opts.AuthHost.TrimEnd('/')}/token/json/2/user/new");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.DeviceToken);
        // A POST with no body needs an explicit zero-length payload or
        // reMarkable returns 400.
        req.Content = new StringContent("", Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            // 401 almost always means the device token has been revoked on
            // the cloud side (user disconnected the device from the website).
            if ((int)resp.StatusCode == 401)
                throw new RemarkableException(
                    "reMarkable rejected the stored device token. Disconnect and re-pair on the Settings page.");
            throw new RemarkableException(
                $"Could not refresh reMarkable user token ({(int)resp.StatusCode}). {TrimForMessage(body)}");
        }

        var userToken = body.Trim();
        auth.CachedUserToken = userToken;
        auth.UserTokenExpiresAt = ExtractJwtExpiry(userToken) ?? DateTime.UtcNow.AddMinutes(55);
        await _db.SaveChangesAsync(ct);
        return userToken;
    }

    // Pulls `exp` (unix seconds) out of the JWT payload so we can cache the
    // token rather than re-fetching on every upload. Returns null if the
    // token isn't a recognizable JWT — caller falls back to a 55-min guess.
    private static DateTime? ExtractJwtExpiry(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1];
            // Base64URL → base64 + padding.
            payload = payload.Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds))
                return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }
        catch { /* malformed — fall back to default expiry */ }
        return null;
    }

    private static string TrimForMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var s = body.Trim();
        return s.Length > 300 ? s[..300] + "…" : s;
    }
}

public sealed class RemarkableException : Exception
{
    public RemarkableException(string message) : base(message) { }
    public RemarkableException(string message, Exception inner) : base(message, inner) { }
}

public sealed class RemarkableOptions
{
    // Defaults match the hosts used by current open-source clients (rmapi,
    // rmapi-js). Override in appsettings.json if reMarkable moves them.
    public string AuthHost { get; set; } = "https://webapp.cloud.remarkable.com";
    public string ApiHost { get; set; } = "https://internal.cloud.remarkable.com";

    // Sent on /device/new. Must be one of the reMarkable-accepted strings:
    // desktop-windows, desktop-macos, desktop-linux, mobile-android,
    // mobile-ios, browser-chrome, remarkable.
    public string DeviceDescription { get; set; } = "desktop-windows";
}

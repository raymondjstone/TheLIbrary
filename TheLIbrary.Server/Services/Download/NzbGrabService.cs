using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;

namespace TheLibrary.Server.Services.Download;

// Optional "grab" automation: search a Newznab indexer for a wanted book and
// hand the best NZB to SABnzbd. Entirely config-driven (Settings page); when the
// indexer or SAB isn't configured the feature reports unavailable and the UI
// hides the button. Best-effort and defensive — indexer JSON shapes vary.
public sealed class NzbGrabService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NzbGrabService> _log;

    public NzbGrabService(IHttpClientFactory httpFactory, IServiceScopeFactory scopeFactory, ILogger<NzbGrabService> log)
    {
        _httpFactory = httpFactory; _scopeFactory = scopeFactory; _log = log;
    }

    public sealed record Config(string? NewznabUrl, string? NewznabKey, string? SabUrl, string? SabKey, string? SabCategory)
    {
        public bool IndexerReady => !string.IsNullOrWhiteSpace(NewznabUrl) && !string.IsNullOrWhiteSpace(NewznabKey);
        public bool SabReady => !string.IsNullOrWhiteSpace(SabUrl) && !string.IsNullOrWhiteSpace(SabKey);
        public bool Ready => IndexerReady && SabReady;
    }

    public async Task<Config> GetConfigAsync(LibraryDbContext db, CancellationToken ct)
    {
        var s = await db.AppSettings.AsNoTracking()
            .Where(x => x.Key == AppSettingKeys.NewznabUrl || x.Key == AppSettingKeys.NewznabApiKey
                     || x.Key == AppSettingKeys.SabnzbdUrl || x.Key == AppSettingKeys.SabnzbdApiKey
                     || x.Key == AppSettingKeys.SabnzbdCategory)
            .ToDictionaryAsync(x => x.Key, x => x.Value, ct);
        string? V(string k) => s.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        return new Config(V(AppSettingKeys.NewznabUrl), V(AppSettingKeys.NewznabApiKey),
            V(AppSettingKeys.SabnzbdUrl), V(AppSettingKeys.SabnzbdApiKey), V(AppSettingKeys.SabnzbdCategory));
    }

    public sealed record GrabResult(bool Success, string Message, string? Title);

    public async Task<GrabResult> GrabAsync(int bookId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var cfg = await GetConfigAsync(db, ct);
        if (!cfg.Ready)
            return new GrabResult(false, "Download client / indexer not configured (Settings → Download automation).", null);

        var book = await db.Books.AsNoTracking()
            .Where(b => b.Id == bookId)
            .Select(b => new { b.Title, Author = b.Author.Name })
            .FirstOrDefaultAsync(ct);
        if (book is null) return new GrabResult(false, "Book not found.", null);

        var query = $"{book.Author} {book.Title}".Trim();
        var (nzbUrl, hit) = await SearchAsync(cfg, query, ct);
        if (nzbUrl is null)
            return new GrabResult(false, $"No indexer results for \"{query}\".", book.Title);

        var ok = await SendToSabAsync(cfg, nzbUrl, ct);
        return ok
            ? new GrabResult(true, $"Sent to SABnzbd: {hit}", book.Title)
            : new GrabResult(false, "Indexer found a result but SABnzbd rejected it (check URL/API key).", book.Title);
    }

    // Returns (nzbDownloadUrl, resultTitle) of the top Newznab hit, or (null,null).
    private async Task<(string? Url, string? Title)> SearchAsync(Config cfg, string query, CancellationToken ct)
    {
        var baseUrl = cfg.NewznabUrl!.TrimEnd('/');
        // Newznab book search. cat 7000/8000 ranges are Books/Ebooks across most indexers.
        var url = $"{baseUrl}/api?t=search&q={Uri.EscapeDataString(query)}&apikey={Uri.EscapeDataString(cfg.NewznabKey!)}&o=json&limit=25&cat=7000,7020,8000,8010";
        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(30);
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Newznab search failed: {Status} for {Query}", resp.StatusCode, query);
                return (null, null);
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            return ParseFirstNzb(json);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Newznab search error for {Query}", query);
            return (null, null);
        }
    }

    // Newznab JSON: { "channel": { "item": [ { "title", "enclosure": { "@attributes": { "url" } }, "link" } ] } }.
    // item can be an object (single result) or an array. Defensive throughout.
    private static (string? Url, string? Title) ParseFirstNzb(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("channel", out var channel)) return (null, null);
            if (!channel.TryGetProperty("item", out var item)) return (null, null);

            JsonElement first;
            if (item.ValueKind == JsonValueKind.Array)
            {
                if (item.GetArrayLength() == 0) return (null, null);
                first = item[0];
            }
            else if (item.ValueKind == JsonValueKind.Object) first = item;
            else return (null, null);

            var title = first.TryGetProperty("title", out var t) ? t.GetString() : null;
            string? nzbUrl = null;
            if (first.TryGetProperty("enclosure", out var enc))
            {
                var e = enc.ValueKind == JsonValueKind.Array && enc.GetArrayLength() > 0 ? enc[0] : enc;
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("@attributes", out var attrs)
                    && attrs.TryGetProperty("url", out var u))
                    nzbUrl = u.GetString();
            }
            nzbUrl ??= first.TryGetProperty("link", out var link) ? link.GetString() : null;
            return string.IsNullOrWhiteSpace(nzbUrl) ? (null, null) : (nzbUrl, title);
        }
        catch { return (null, null); }
    }

    private async Task<bool> SendToSabAsync(Config cfg, string nzbUrl, CancellationToken ct)
    {
        var baseUrl = cfg.SabUrl!.TrimEnd('/');
        var url = $"{baseUrl}/api?mode=addurl&name={Uri.EscapeDataString(nzbUrl)}&apikey={Uri.EscapeDataString(cfg.SabKey!)}&output=json";
        if (!string.IsNullOrWhiteSpace(cfg.SabCategory))
            url += $"&cat={Uri.EscapeDataString(cfg.SabCategory)}";
        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(30);
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return false;
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("status", out var st)
                && (st.ValueKind == JsonValueKind.True
                    || (st.ValueKind == JsonValueKind.String && bool.TryParse(st.GetString(), out var b) && b));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SABnzbd addurl error");
            return false;
        }
    }
}

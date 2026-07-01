using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;

namespace TheLibrary.Server.Services.OpenLibrary;

// The bits of a Google Books volume we care about for ISBN resolution. Google
// Books has no OpenLibrary work key, so this is title/author/year only — enough to
// show "what this ISBN is" on the Identified page for self-published / indie / KDP
// titles OpenLibrary doesn't hold.
public sealed record GoogleBookInfo(string? Title, string? Author, int? FirstPublishYear);

// Thin client over the Google Books volumes API, used as a FALLBACK when
// OpenLibrary has no record of an ISBN. Opt-in: only called when a Google Books
// API key is configured. Returns null for a clean "not found" (so the miss is
// cached); throws on a transient HTTP failure (so nothing is cached and it's
// retried later — same contract the OpenLibrary path relies on).
public sealed class GoogleBooksClient
{
    private readonly HttpClient _http;
    private readonly GoogleBooksRateLimiter _limiter;
    private readonly ILogger<GoogleBooksClient> _log;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public GoogleBooksClient(HttpClient http, GoogleBooksRateLimiter limiter, ILogger<GoogleBooksClient> log)
    {
        _http = http;
        _limiter = limiter;
        _log = log;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri("https://www.googleapis.com/books/v1/");
    }

    // Resolve an ISBN to its first Google Books volume. `isbn` is expected already
    // normalized (bare digits). Returns null on a clean not-found; throws
    // GoogleBooksQuotaExceededException when the quota is spent, or HttpRequestException
    // on another transient failure — either way the caller doesn't cache the result,
    // so it's retried later (the next day once the daily quota resets).
    public async Task<GoogleBookInfo?> ResolveByIsbnAsync(string isbn, string apiKey, CancellationToken ct)
    {
        // Throws immediately if today's quota is already latched (no HTTP call made),
        // else waits out the per-minute spacing.
        await _limiter.ReserveAsync(ct);

        var url = $"volumes?q=isbn:{HttpUtility.UrlEncode(isbn)}&country=US&maxResults=1&fields=totalItems,items(volumeInfo(title,authors,publishedDate))";
        if (!string.IsNullOrWhiteSpace(apiKey))
            url += $"&key={HttpUtility.UrlEncode(apiKey)}";

        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var status = (int)resp.StatusCode;
            // 429, or a 403 whose body cites a quota/rate reason, means the daily quota
            // is spent: latch it so the rest of today's lookups short-circuit, and
            // signal "retry later" so nothing is cached.
            var isQuota = status == 429;
            if (status == 403)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                isQuota = errBody.Contains("imitExceeded", StringComparison.OrdinalIgnoreCase)
                       || errBody.Contains("quota", StringComparison.OrdinalIgnoreCase);
            }
            if (isQuota)
            {
                _limiter.MarkExhausted();
                _log.LogWarning("Google Books quota reached (HTTP {Status}) — pausing Google lookups until tomorrow", status);
                throw new GoogleBooksQuotaExceededException($"Google Books returned HTTP {status}");
            }
            _log.LogWarning("Google Books ISBN {Isbn} returned HTTP {Status}", isbn, status);
            throw new HttpRequestException($"Google Books returned HTTP {status} for ISBN {isbn}");
        }

        var body = await resp.Content.ReadFromJsonAsync<VolumesResponse>(JsonOpts, ct);
        var info = body?.Items?.FirstOrDefault()?.VolumeInfo;
        if (info is null || string.IsNullOrWhiteSpace(info.Title)) return null; // clean not-found

        return new GoogleBookInfo(
            info.Title.Trim(),
            info.Authors is { Count: > 0 } ? info.Authors[0] : null,
            ParseYear(info.PublishedDate));
    }

    // Google's publishedDate is "2019", "2019-05", or "2019-05-01" — take the year.
    private static int? ParseYear(string? published)
    {
        if (string.IsNullOrWhiteSpace(published)) return null;
        var head = published.Length >= 4 ? published[..4] : published;
        return int.TryParse(head, out var y) && y is > 0 and < 3000 ? y : null;
    }

    private sealed class VolumesResponse
    {
        [JsonPropertyName("totalItems")] public int TotalItems { get; set; }
        [JsonPropertyName("items")] public List<VolumeItem>? Items { get; set; }
    }
    private sealed class VolumeItem
    {
        [JsonPropertyName("volumeInfo")] public VolumeInfo? VolumeInfo { get; set; }
    }
    private sealed class VolumeInfo
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("authors")] public List<string>? Authors { get; set; }
        [JsonPropertyName("publishedDate")] public string? PublishedDate { get; set; }
    }
}

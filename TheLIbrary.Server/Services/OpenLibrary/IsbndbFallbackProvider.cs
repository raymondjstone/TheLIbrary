using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using TheLibrary.Server.Data.Models;

namespace TheLibrary.Server.Services.OpenLibrary;

// ISBNdb (api2.isbndb.com) — a paid, comprehensive ISBN database with strong
// coverage of self-published / KDP / print-on-demand / foreign titles. Auth is the
// bare API key in the Authorization header. GET /book/{isbn}. Rate-limited per plan
// (basic ≈ 1 req/sec), so calls are throttled.
public sealed class IsbndbFallbackProvider : IIsbnFallbackProvider
{
    public const string HttpClientName = "isbndb";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<IsbndbFallbackProvider> _log;
    // Basic plan is ~1 request/second — space calls just over that.
    private readonly IsbnSourceThrottle _throttle = new(TimeSpan.FromMilliseconds(1100));
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public IsbndbFallbackProvider(IHttpClientFactory httpFactory, ILogger<IsbndbFallbackProvider> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    public string Name => "ISBNdb";
    public string CredentialSettingKey => AppSettingKeys.IsbndbApiKey;

    public async Task<IsbnLookupResult> LookupAsync(string isbn, string? credential, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(credential)) return IsbnLookupResult.Skipped;

        await _throttle.WaitAsync(ct);
        var http = _httpFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"book/{HttpUtility.UrlEncode(isbn)}");
        req.Headers.TryAddWithoutValidation("Authorization", credential.Trim());

        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct); }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "ISBNdb lookup for {Isbn} failed transiently", isbn);
            return IsbnLookupResult.Unavailable;
        }
        using (resp)
        {
            switch (resp.StatusCode)
            {
                case HttpStatusCode.NotFound:
                    return IsbnLookupResult.Miss;
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    // Bad/expired key — treat as "off" (skip) rather than block caching
                    // of genuine misses. Logged so the misconfig is visible.
                    _log.LogWarning("ISBNdb returned {Status} for {Isbn} — check the API key", (int)resp.StatusCode, isbn);
                    return IsbnLookupResult.Skipped;
                case HttpStatusCode.TooManyRequests:
                    return IsbnLookupResult.Unavailable;
            }
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("ISBNdb returned HTTP {Status} for {Isbn}", (int)resp.StatusCode, isbn);
                return IsbnLookupResult.Unavailable;
            }

            var body = await resp.Content.ReadFromJsonAsync<IsbndbResponse>(JsonOpts, ct);
            var book = body?.Book;
            var title = book?.Title ?? book?.TitleLong;
            if (book is null || string.IsNullOrWhiteSpace(title)) return IsbnLookupResult.Miss;
            return IsbnLookupResult.Found(
                title.Trim(),
                book.Authors is { Count: > 0 } ? book.Authors[0] : null,
                ParseYear(book.DatePublished));
        }
    }

    // date_published is "2019", "2019-05", "2019-05-01", or occasionally free text.
    private static int? ParseYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date)) return null;
        var head = date.Length >= 4 ? date[..4] : date;
        return int.TryParse(head, out var y) && y is > 0 and < 3000 ? y : null;
    }

    private sealed class IsbndbResponse
    {
        [JsonPropertyName("book")] public IsbndbBook? Book { get; set; }
    }
    private sealed class IsbndbBook
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("title_long")] public string? TitleLong { get; set; }
        [JsonPropertyName("authors")] public List<string>? Authors { get; set; }
        [JsonPropertyName("date_published")] public string? DatePublished { get; set; }
    }
}

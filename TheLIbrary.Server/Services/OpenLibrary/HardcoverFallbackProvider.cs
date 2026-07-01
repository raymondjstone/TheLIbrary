using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TheLibrary.Server.Data.Models;

namespace TheLibrary.Server.Services.OpenLibrary;

// Hardcover (api.hardcover.app) — a free, community-driven book database (Hasura
// GraphQL) whose readership skews heavily to indie / KDP / romance, so its coverage
// of the self-published tail can beat OpenLibrary's. Auth is a Bearer token. We match
// an edition by isbn_13 or isbn_10 and read its title + first author.
//
// NOTE: Hardcover's GraphQL schema is community-maintained and can change. The query
// below is best-effort; if it ever returns GraphQL errors the provider treats the
// source as "off" (Skipped) and logs it, rather than poisoning the chain — so a
// schema drift degrades gracefully instead of blocking cached misses.
public sealed class HardcoverFallbackProvider : IIsbnFallbackProvider
{
    public const string HttpClientName = "hardcover";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<HardcoverFallbackProvider> _log;
    private readonly IsbnSourceThrottle _throttle = new(TimeSpan.FromMilliseconds(1100)); // ~55/min
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private const string Query =
        "query($isbn: String!) { editions(where: {_or: [{isbn_13: {_eq: $isbn}}, {isbn_10: {_eq: $isbn}}]}, limit: 1) " +
        "{ title book { title contributions(limit: 1) { author { name } } } } }";

    public HardcoverFallbackProvider(IHttpClientFactory httpFactory, ILogger<HardcoverFallbackProvider> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    public string Name => "Hardcover";
    public string CredentialSettingKey => AppSettingKeys.HardcoverApiToken;

    public async Task<IsbnLookupResult> LookupAsync(string isbn, string? credential, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(credential)) return IsbnLookupResult.Skipped;

        await _throttle.WaitAsync(ct);
        var http = _httpFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Post, "graphql")
        {
            Content = JsonContent.Create(new { query = Query, variables = new { isbn } }),
        };
        var token = credential.Trim();
        req.Headers.TryAddWithoutValidation("Authorization",
            token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? token : $"Bearer {token}");

        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct); }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "Hardcover lookup for {Isbn} failed transiently", isbn);
            return IsbnLookupResult.Unavailable;
        }
        using (resp)
        {
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _log.LogWarning("Hardcover returned {Status} for {Isbn} — check the API token", (int)resp.StatusCode, isbn);
                return IsbnLookupResult.Skipped;
            }
            if (resp.StatusCode == HttpStatusCode.TooManyRequests) return IsbnLookupResult.Unavailable;
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Hardcover returned HTTP {Status} for {Isbn}", (int)resp.StatusCode, isbn);
                return IsbnLookupResult.Unavailable;
            }

            var body = await resp.Content.ReadFromJsonAsync<GraphQlResponse>(JsonOpts, ct);
            if (body?.Errors is { Count: > 0 })
            {
                // Schema drift or a bad query — don't retry-loop; treat as off.
                _log.LogWarning("Hardcover GraphQL error for {Isbn}: {Msg}", isbn, body.Errors[0].Message);
                return IsbnLookupResult.Skipped;
            }

            var edition = body?.Data?.Editions?.FirstOrDefault();
            if (edition is null) return IsbnLookupResult.Miss;
            var title = string.IsNullOrWhiteSpace(edition.Title) ? edition.Book?.Title : edition.Title;
            if (string.IsNullOrWhiteSpace(title)) return IsbnLookupResult.Miss;
            var author = edition.Book?.Contributions?.FirstOrDefault()?.Author?.Name;
            return IsbnLookupResult.Found(title!.Trim(), author, null);
        }
    }

    private sealed class GraphQlResponse
    {
        [JsonPropertyName("data")] public GraphQlData? Data { get; set; }
        [JsonPropertyName("errors")] public List<GraphQlError>? Errors { get; set; }
    }
    private sealed class GraphQlError { [JsonPropertyName("message")] public string? Message { get; set; } }
    private sealed class GraphQlData { [JsonPropertyName("editions")] public List<Edition>? Editions { get; set; } }
    private sealed class Edition
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("book")] public HcBook? Book { get; set; }
    }
    private sealed class HcBook
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("contributions")] public List<Contribution>? Contributions { get; set; }
    }
    private sealed class Contribution { [JsonPropertyName("author")] public HcAuthor? Author { get; set; } }
    private sealed class HcAuthor { [JsonPropertyName("name")] public string? Name { get; set; } }
}

using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace TheLibrary.Server.Services.Llm;

// Best-effort provider SPEND (not remaining balance — neither provider exposes a
// credit balance via the API). Reads each provider's org-level cost API, which
// needs a separate ADMIN key. Results are cached for an hour and any failure
// returns null, so the Health card simply omits a provider it can't read. The
// cost endpoints are beta and parsed defensively.
public sealed class LlmSpendClient
{
    public const int WindowDays = 30;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly ILogger<LlmSpendClient> _log;

    public LlmSpendClient(HttpClient http, IMemoryCache cache, ILogger<LlmSpendClient> log)
    {
        _http = http;
        _cache = cache;
        _log = log;
    }

    public Task<decimal?> GetOpenAiSpendAsync(string? adminKey, CancellationToken ct)
        => GetCachedAsync("llm-spend:openai", adminKey, FetchOpenAiAsync, ct);

    public Task<decimal?> GetAnthropicSpendAsync(string? adminKey, CancellationToken ct)
        => GetCachedAsync("llm-spend:anthropic", adminKey, FetchAnthropicAsync, ct);

    private async Task<decimal?> GetCachedAsync(
        string cacheKey, string? adminKey, Func<string, CancellationToken, Task<decimal?>> fetch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(adminKey)) return null;
        if (_cache.TryGetValue(cacheKey, out decimal? cached)) return cached;
        var value = await fetch(adminKey!, ct);
        // Cache a success for an hour; cache a failure only briefly so a transient
        // error doesn't hide spend for the whole hour.
        _cache.Set(cacheKey, value, value is null ? TimeSpan.FromMinutes(2) : CacheTtl);
        return value;
    }

    // OpenAI Costs API (needs an admin key). Sums daily buckets over the window.
    private async Task<decimal?> FetchOpenAiAsync(string adminKey, CancellationToken ct)
    {
        try
        {
            var start = DateTimeOffset.UtcNow.AddDays(-WindowDays).ToUnixTimeSeconds();
            var url = $"https://api.openai.com/v1/organization/costs?start_time={start}&bucket_width=1d&limit=31";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminKey);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("OpenAI costs API returned {Status}", (int)resp.StatusCode);
                return null;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return SumBuckets(doc.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _log.LogWarning(ex, "OpenAI costs fetch failed");
            return null;
        }
    }

    // Anthropic Cost Report (Admin API; needs an admin key). Same shape: daily
    // buckets, each with results carrying an amount.
    private async Task<decimal?> FetchAnthropicAsync(string adminKey, CancellationToken ct)
    {
        try
        {
            var start = DateTime.UtcNow.AddDays(-WindowDays).ToString("yyyy-MM-ddT00:00:00Z");
            var url = $"https://api.anthropic.com/v1/organizations/cost_report?starting_at={start}&bucket_width=1d";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("x-api-key", adminKey);
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Anthropic cost_report returned {Status}", (int)resp.StatusCode);
                return null;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return SumBuckets(doc.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _log.LogWarning(ex, "Anthropic cost_report fetch failed");
            return null;
        }
    }

    // Both APIs return { data: [ { results: [ { amount: <number|string|{value}> } ] } ] }.
    internal static decimal SumBuckets(JsonElement root)
    {
        decimal total = 0;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return total;
        foreach (var bucket in data.EnumerateArray())
        {
            if (!bucket.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) continue;
            foreach (var r in results.EnumerateArray())
                if (r.TryGetProperty("amount", out var amt))
                    total += ParseAmount(amt);
        }
        return total;
    }

    private static decimal ParseAmount(JsonElement amt)
    {
        // OpenAI: amount: { value: 0.06, currency: "usd" }; Anthropic: amount may be
        // a bare number or a string.
        if (amt.ValueKind == JsonValueKind.Object && amt.TryGetProperty("value", out var v))
            amt = v;
        if (amt.ValueKind == JsonValueKind.Number) return amt.GetDecimal();
        if (amt.ValueKind == JsonValueKind.String
            && decimal.TryParse(amt.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return d;
        return 0;
    }
}

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;

namespace TheLibrary.Server.Services.Llm;

// All the signals we already hold about a file, fed to the model together so it
// has the best chance of naming the book — filename, embedded metadata, ISBN, and
// a front-matter text snippet.
public sealed record LlmSignals(
    string FileName, string? EmbeddedTitle, string? EmbeddedAuthor, string? Isbn, string? FrontMatter);

public sealed record LlmGuess(string? Title, string? Author, string? Series, string? SeriesPosition, double Confidence);

public sealed record LlmConfig(bool Enabled, string Provider, string? ApiKey, string Model, string BaseUrl, int MaxPerRun, int MaxPerDay)
{
    public bool Ready => Enabled && !string.IsNullOrWhiteSpace(ApiKey);
}

// Provider-agnostic LLM metadata extractor. Speaks both the Anthropic Messages
// API (Claude) and the OpenAI Chat Completions API (ChatGPT); the active provider
// is chosen in Settings. Network-bound and best-effort: any failure returns null
// rather than throwing, so the calling job just skips the file. The returned
// title/author is always re-validated against OpenLibrary downstream, so a
// hallucinated guess can never be filed.
public sealed class LlmMetadataClient
{
    public const string DefaultAnthropicModel = "claude-haiku-4-5-20251001";
    public const string DefaultOpenAiModel = "gpt-4o-mini";
    public const string AnthropicBaseUrl = "https://api.anthropic.com";
    public const string OpenAiBaseUrl = "https://api.openai.com";
    public const int DefaultMaxPerRun = 50;
    public const int DefaultMaxPerDay = 500;

    private const string SystemPrompt =
        "You identify books from messy metadata. Given a filename, any embedded title/author, "
        + "an ISBN, and a snippet of the book's opening text, return the work's real Title and "
        + "Author. Return ONLY compact JSON: {\"title\":string|null,\"author\":string|null,"
        + "\"series\":string|null,\"series_position\":string|null,\"confidence\":number}. "
        + "Author is a single person's full name (no roles, publishers, or 'Unknown'). "
        + "confidence is 0..1. Use null when you genuinely cannot tell — do not guess wildly.";

    private readonly HttpClient _http;
    private readonly ILogger<LlmMetadataClient> _log;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public LlmMetadataClient(HttpClient http, ILogger<LlmMetadataClient> log)
    {
        _http = http;
        _log = log;
    }

    public static async Task<LlmConfig> LoadConfigAsync(LibraryDbContext db, CancellationToken ct)
    {
        var s = await db.AppSettings.AsNoTracking()
            .Where(x => x.Key == AppSettingKeys.LlmEnabled || x.Key == AppSettingKeys.LlmProvider
                     || x.Key == AppSettingKeys.LlmApiKey || x.Key == AppSettingKeys.LlmModel
                     || x.Key == AppSettingKeys.LlmBaseUrl || x.Key == AppSettingKeys.LlmMaxPerRun
                     || x.Key == AppSettingKeys.LlmMaxPerDay)
            .ToDictionaryAsync(x => x.Key, x => x.Value, ct);
        string? V(string k) => s.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        int I(string k, int dflt) => int.TryParse(V(k), out var n) && n > 0 ? n : dflt;

        var provider = (V(AppSettingKeys.LlmProvider) ?? "anthropic").ToLowerInvariant();
        var isOpenAi = provider == "openai";
        var model = V(AppSettingKeys.LlmModel) ?? (isOpenAi ? DefaultOpenAiModel : DefaultAnthropicModel);
        var baseUrl = (V(AppSettingKeys.LlmBaseUrl) ?? (isOpenAi ? OpenAiBaseUrl : AnthropicBaseUrl)).TrimEnd('/');
        return new LlmConfig(
            Enabled: string.Equals(V(AppSettingKeys.LlmEnabled), "true", StringComparison.OrdinalIgnoreCase),
            Provider: isOpenAi ? "openai" : "anthropic",
            ApiKey: V(AppSettingKeys.LlmApiKey),
            Model: model, BaseUrl: baseUrl,
            MaxPerRun: I(AppSettingKeys.LlmMaxPerRun, DefaultMaxPerRun),
            MaxPerDay: I(AppSettingKeys.LlmMaxPerDay, DefaultMaxPerDay));
    }

    // One identification call. Returns null on any error or an empty result.
    public async Task<LlmGuess?> IdentifyAsync(LlmConfig cfg, LlmSignals signals, CancellationToken ct)
    {
        if (!cfg.Ready) return null;
        try
        {
            using var req = cfg.Provider == "openai" ? BuildOpenAi(cfg, signals) : BuildAnthropic(cfg, signals);
            using var resp = await _http.SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("LLM identify failed: {Status} {Body}", (int)resp.StatusCode, Trim(raw));
                return null;
            }
            var text = cfg.Provider == "openai" ? ExtractOpenAiText(raw) : ExtractAnthropicText(raw);
            return ParseGuess(text);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _log.LogWarning(ex, "LLM identify error");
            return null;
        }
    }

    private HttpRequestMessage BuildAnthropic(LlmConfig cfg, LlmSignals s)
    {
        var body = new
        {
            model = cfg.Model,
            max_tokens = 300,
            system = SystemPrompt,
            messages = new[] { new { role = "user", content = UserContent(s) } },
        };
        var req = new HttpRequestMessage(HttpMethod.Post, $"{cfg.BaseUrl}/v1/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("x-api-key", cfg.ApiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        return req;
    }

    private HttpRequestMessage BuildOpenAi(LlmConfig cfg, LlmSignals s)
    {
        var body = new
        {
            model = cfg.Model,
            max_tokens = 300,
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = UserContent(s) },
            },
        };
        var req = new HttpRequestMessage(HttpMethod.Post, $"{cfg.BaseUrl}/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
        return req;
    }

    private static string UserContent(LlmSignals s)
    {
        var sb = new StringBuilder();
        sb.Append("Filename: ").Append(s.FileName).Append('\n');
        if (!string.IsNullOrWhiteSpace(s.EmbeddedTitle)) sb.Append("Embedded title: ").Append(s.EmbeddedTitle).Append('\n');
        if (!string.IsNullOrWhiteSpace(s.EmbeddedAuthor)) sb.Append("Embedded author: ").Append(s.EmbeddedAuthor).Append('\n');
        if (!string.IsNullOrWhiteSpace(s.Isbn)) sb.Append("ISBN: ").Append(s.Isbn).Append('\n');
        if (!string.IsNullOrWhiteSpace(s.FrontMatter))
            sb.Append("Opening text:\n").Append(s.FrontMatter!.Length > 4000 ? s.FrontMatter[..4000] : s.FrontMatter);
        return sb.ToString();
    }

    private static string? ExtractAnthropicText(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0
            && c[0].TryGetProperty("text", out var t) ? t.GetString() : null;
    }

    private static string? ExtractOpenAiText(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.TryGetProperty("choices", out var ch) && ch.ValueKind == JsonValueKind.Array && ch.GetArrayLength() > 0
            && ch[0].TryGetProperty("message", out var m) && m.TryGetProperty("content", out var cnt)
            ? cnt.GetString() : null;
    }

    // The models are told to return bare JSON, but tolerate code fences / prose
    // around it by extracting the first {...} block.
    private static LlmGuess? ParseGuess(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        var json = text[start..(end + 1)];
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string? Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;
            double conf = r.TryGetProperty("confidence", out var cv) && cv.ValueKind == JsonValueKind.Number ? cv.GetDouble() : 0;
            var author = Str("author");
            if (string.Equals(author, "Unknown", StringComparison.OrdinalIgnoreCase)) author = null;
            var guess = new LlmGuess(Str("title"), author, Str("series"), Str("series_position"), conf);
            return guess.Title is null && guess.Author is null ? null : guess;
        }
        catch (JsonException) { return null; }
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;
}

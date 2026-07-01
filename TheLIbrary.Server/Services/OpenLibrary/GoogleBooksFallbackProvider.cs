using TheLibrary.Server.Data.Models;

namespace TheLibrary.Server.Services.OpenLibrary;

// Google Books as a fallback provider — wraps the existing GoogleBooksClient (which
// carries the per-minute pacing and daily-quota latch). A spent quota surfaces as
// Unavailable (retry later), a clean not-found as Miss.
public sealed class GoogleBooksFallbackProvider : IIsbnFallbackProvider
{
    private readonly GoogleBooksClient _client;
    private readonly ILogger<GoogleBooksFallbackProvider> _log;

    public GoogleBooksFallbackProvider(GoogleBooksClient client, ILogger<GoogleBooksFallbackProvider> log)
    {
        _client = client;
        _log = log;
    }

    public string Name => "Google Books";
    public string CredentialSettingKey => AppSettingKeys.GoogleBooksApiKey;

    public async Task<IsbnLookupResult> LookupAsync(string isbn, string? credential, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(credential)) return IsbnLookupResult.Skipped;
        try
        {
            var gb = await _client.ResolveByIsbnAsync(isbn, credential, ct);
            return gb is not null && !string.IsNullOrWhiteSpace(gb.Title)
                ? IsbnLookupResult.Found(gb.Title!, gb.Author, gb.FirstPublishYear)
                : IsbnLookupResult.Miss;
        }
        catch (GoogleBooksQuotaExceededException) { return IsbnLookupResult.Unavailable; }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "Google Books lookup for {Isbn} failed transiently", isbn);
            return IsbnLookupResult.Unavailable;
        }
    }
}

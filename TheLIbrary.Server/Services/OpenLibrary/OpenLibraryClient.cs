using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;

namespace TheLibrary.Server.Services.OpenLibrary;

public sealed class OpenLibraryClient
{
    private readonly HttpClient _http;
    private readonly OpenLibraryRateLimiter _limiter;
    private readonly ILogger<OpenLibraryClient> _log;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public OpenLibraryClient(HttpClient http, OpenLibraryRateLimiter limiter, ILogger<OpenLibraryClient> log)
    {
        _http = http;
        _limiter = limiter;
        _log = log;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri("https://openlibrary.org/");
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("TheLibrary/1.0 (self-hosted collection manager)");
    }

    public Task<AuthorSearchResponse?> SearchAuthorsAsync(string name, CancellationToken ct)
    {
        var url = $"search/authors.json?q={HttpUtility.UrlEncode(name)}&limit=10";
        return GetJsonAsync<AuthorSearchResponse>(url, ct);
    }

    // Fetches the day's merge-authors changelog. Each entry has a surviving
    // master author key plus the duplicate keys that were folded into it;
    // callers use this to rewrite local OpenLibraryKey pointers that still
    // reference a now-deleted duplicate. Returns an empty list if the day
    // has no merges (the endpoint can 404 or return []).
    public async Task<IReadOnlyList<AuthorMergeChange>> FetchAuthorMergesAsync(DateOnly date, CancellationToken ct)
    {
        var url = $"recentchanges/{date.Year:0000}/{date.Month:00}/{date.Day:00}/merge-authors.json";
        var list = await GetJsonAsync<List<AuthorMergeChange>>(url, ct);
        return list ?? (IReadOnlyList<AuthorMergeChange>)Array.Empty<AuthorMergeChange>();
    }

    // Paged fetch of English works for a given author key. Uses the main search
    // with author_key= and language=eng so variants/editions collapse to works.
    public async IAsyncEnumerable<WorkSearchDoc> GetEnglishWorksAsync(
        string authorKey,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        const int pageSize = 100;
        const string fields = "key,title,first_publish_year,cover_i,language,author_key,author_name,edition_count";

        int page = 1;
        while (true)
        {
            var url = $"search.json?author_key={HttpUtility.UrlEncode(authorKey)}&language=eng&limit={pageSize}&page={page}&fields={fields}";
            var resp = await GetJsonAsync<WorkSearchResponse>(url, ct);
            if (resp is null || resp.Docs.Count == 0) yield break;

            foreach (var doc in resp.Docs) yield return doc;

            if (resp.Docs.Count < pageSize) yield break;
            if (page * pageSize >= resp.NumFound) yield break;
            page++;
        }
    }

    private async Task<T?> GetJsonAsync<T>(string relativeUrl, CancellationToken ct)
    {
        const int maxAttempts = 5;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await _limiter.RunAsync(async () =>
                {
                    using var resp = await _http.GetAsync(relativeUrl, ct);
                    if (resp.StatusCode == HttpStatusCode.NotFound) return default;
                    if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
                    {
                        var retryAfter = resp.Headers.RetryAfter?.Delta
                                         ?? TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
                        _log.LogWarning("OpenLibrary {Status} for {Url} — waiting {Delay}s (attempt {Attempt})",
                            (int)resp.StatusCode, relativeUrl, retryAfter.TotalSeconds, attempt);
                        await Task.Delay(retryAfter, ct);
                        throw new TransientException();
                    }
                    resp.EnsureSuccessStatusCode();
                    await using var s = await resp.Content.ReadAsStreamAsync(ct);
                    return await JsonSerializer.DeserializeAsync<T>(s, JsonOpts, ct);
                }, ct);
            }
            catch (TransientException) when (attempt < maxAttempts)
            {
                // loop to retry
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts)
            {
                _log.LogWarning(ex, "OpenLibrary request failed for {Url} (attempt {Attempt})", relativeUrl, attempt);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt))), ct);
            }
        }
        _log.LogError("OpenLibrary request gave up after {N} attempts: {Url}", maxAttempts, relativeUrl);
        return default;
    }

    private sealed class TransientException : Exception { }
}

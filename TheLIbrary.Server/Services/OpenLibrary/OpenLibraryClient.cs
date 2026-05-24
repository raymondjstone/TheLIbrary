using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;

namespace TheLibrary.Server.Services.OpenLibrary;

public sealed class OpenLibraryRequestFailedException : Exception
{
    public OpenLibraryRequestFailedException(string relativeUrl)
        : base($"OpenLibrary request failed after multiple attempts: {relativeUrl}")
    {
        RelativeUrl = relativeUrl;
    }

    public string RelativeUrl { get; }
}

public sealed class OpenLibraryClient
{
    private readonly HttpClient _http;
    private readonly OpenLibraryRateLimiter _limiter;
    private readonly OpenLibrarySettings _settings;
    private readonly ILogger<OpenLibraryClient> _log;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public OpenLibraryClient(
        HttpClient http, OpenLibraryRateLimiter limiter,
        OpenLibrarySettings settings, ILogger<OpenLibraryClient> log)
    {
        _http = http;
        _limiter = limiter;
        _settings = settings;
        _log = log;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri("https://openlibrary.org/");
    }

    public Task<AuthorSearchResponse?> SearchAuthorsAsync(string name, CancellationToken ct)
    {
        var url = $"search/authors.json?q={HttpUtility.UrlEncode(name)}&limit=10";
        return GetJsonAsync<AuthorSearchResponse>(url, ct);
    }

    public Task<WorkSearchResponse?> SearchWorksAsync(string title, string? author, CancellationToken ct)
    {
        var fields = "key,title,first_publish_year,cover_i,author_name,author_key";
        var url = $"search.json?title={HttpUtility.UrlEncode(title)}&limit=10&fields={fields}";
        if (!string.IsNullOrWhiteSpace(author))
            url += $"&author={HttpUtility.UrlEncode(author.Trim())}";
        return GetJsonAsync<WorkSearchResponse>(url, ct);
    }

    // Fetches a single author record by OL key (e.g. "OL123A"). Returns null on
    // 404 or if the record is a redirect (merged into another key). Upstream
    // failures throw OpenLibraryRequestFailedException so callers can distinguish
    // them from a real miss.
    public Task<AuthorDetailResponse?> FetchAuthorAsync(string key, CancellationToken ct)
        => GetJsonAsync<AuthorDetailResponse>($"authors/{HttpUtility.UrlEncode(key)}.json", ct);

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

    // Paged fetch of English-only works for a given author key.
    public IAsyncEnumerable<WorkSearchDoc> GetEnglishWorksAsync(
        string authorKey, CancellationToken ct)
        => GetWorksAsync(authorKey, "eng", ct);

    // Paged fetch of all works (any language) for a given author key.
    // Used for starred authors where the normal language restriction is waived.
    public IAsyncEnumerable<WorkSearchDoc> GetAllWorksAsync(
        string authorKey, CancellationToken ct)
        => GetWorksAsync(authorKey, null, ct);

    private async IAsyncEnumerable<WorkSearchDoc> GetWorksAsync(
        string authorKey,
        string? language,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        const int pageSize = 100;
        const string fields = "key,title,first_publish_year,cover_i,language,author_key,author_name,edition_count,subject,series";
        var langFilter = string.IsNullOrEmpty(language) ? "" : $"&language={HttpUtility.UrlEncode(language)}";

        int page = 1;
        while (true)
        {
            var url = $"search.json?author_key={HttpUtility.UrlEncode(authorKey)}{langFilter}&limit={pageSize}&page={page}&fields={fields}";
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
                    // The User-Agent is set per request so an edit in the
                    // Settings UI takes effect on the very next call. It's
                    // added without validation because the configured app
                    // name may be a URL — not a legal product token, but fine
                    // on the wire and exactly what OpenLibrary wants for contact.
                    using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
                    request.Headers.TryAddWithoutValidation("User-Agent", _settings.UserAgent);
                    using var resp = await _http.SendAsync(request, ct);
                    if (resp.StatusCode == HttpStatusCode.NotFound) return default;
                    if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
                    {
                        // A 429 means OpenLibrary is rate-limiting us — drop to
                        // the 1 req/sec anonymous pace for the rest of the run.
                        if ((int)resp.StatusCode == 429 && !_limiter.IsDemoted)
                        {
                            _log.LogWarning(
                                "OpenLibrary rate-limited the app (429) — dropping to 1 req/sec until restart");
                            _limiter.Demote();
                        }
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
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < maxAttempts)
            {
                // HttpClient timeout (TaskCanceledException with InnerException=TimeoutException),
                // not an external cancellation — retry it like any other transient failure.
                _log.LogWarning("OpenLibrary request timed out for {Url} (attempt {Attempt})", relativeUrl, attempt);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt))), ct);
            }
        }
        _log.LogError("OpenLibrary request gave up after {N} attempts: {Url}", maxAttempts, relativeUrl);
        throw new OpenLibraryRequestFailedException(relativeUrl);
    }

    private sealed class TransientException : Exception { }
}

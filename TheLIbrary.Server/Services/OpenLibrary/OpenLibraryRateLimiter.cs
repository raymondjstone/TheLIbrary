namespace TheLibrary.Server.Services.OpenLibrary;

// Serializes OpenLibrary calls and enforces a minimum gap between them.
// OpenLibrary allows 1 req/sec for anonymous callers and 3 req/sec for
// identified ones (a User-Agent carrying a contact email). The applicable
// pace is read live from OpenLibrarySettings on every call, so editing the
// contact email in the UI changes the rate immediately.
//
// If OpenLibrary ever rate-limits the app (HTTP 429), Demote() pins the pace
// to the 1 req/sec anonymous gap for the rest of the process — it returns to
// the identified pace only on an app restart.
public sealed class OpenLibraryRateLimiter
{
    // Gaps sit just under each tier's ceiling so timing jitter can't tip a
    // burst over the limit (3/sec → 350 ms, 1/sec → 1100 ms).
    private static readonly TimeSpan IdentifiedInterval = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan AnonymousInterval = TimeSpan.FromMilliseconds(1100);

    private readonly OpenLibrarySettings _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastCallUtc = DateTime.MinValue;
    private volatile bool _demoted;

    public OpenLibraryRateLimiter(OpenLibrarySettings settings)
    {
        _settings = settings;
    }

    // True once OpenLibrary has rate-limited the app during this process run.
    public bool IsDemoted => _demoted;

    // Pin to the 1 req/sec anonymous pace for the rest of the process. Called
    // when OpenLibrary returns HTTP 429; cleared only by an app restart.
    public void Demote() => _demoted = true;

    public async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var interval = !_demoted && _settings.IsIdentified
                ? IdentifiedInterval
                : AnonymousInterval;

            var wait = interval - (DateTime.UtcNow - _lastCallUtc);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);

            try
            {
                return await action();
            }
            finally
            {
                _lastCallUtc = DateTime.UtcNow;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

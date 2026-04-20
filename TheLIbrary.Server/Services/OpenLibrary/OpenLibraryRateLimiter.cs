namespace TheLibrary.Server.Services.OpenLibrary;

// Serializes OpenLibrary calls and enforces a minimum gap between them.
// OpenLibrary publishes a 1 req/sec guidance; we default slightly above that.
public sealed class OpenLibraryRateLimiter
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _minInterval;
    private DateTime _lastCallUtc = DateTime.MinValue;

    public OpenLibraryRateLimiter(TimeSpan? minInterval = null)
    {
        _minInterval = minInterval ?? TimeSpan.FromMilliseconds(1100);
    }

    public async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var wait = _minInterval - (DateTime.UtcNow - _lastCallUtc);
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

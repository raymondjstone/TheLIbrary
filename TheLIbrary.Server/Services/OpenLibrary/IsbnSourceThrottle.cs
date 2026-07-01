namespace TheLibrary.Server.Services.OpenLibrary;

// Minimal fixed-interval throttle so a fallback source's per-second/minute rate
// limit isn't tripped by a burst (e.g. the resolve-isbns job working a backlog).
// Serializes callers and spaces them by MinInterval. Each provider holds its own.
public sealed class IsbnSourceThrottle
{
    private readonly TimeSpan _minInterval;
    private readonly object _lock = new();
    private DateTime _nextAllowedUtc = DateTime.MinValue;

    public IsbnSourceThrottle(TimeSpan minInterval) => _minInterval = minInterval;

    public async Task WaitAsync(CancellationToken ct)
    {
        TimeSpan wait;
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (_nextAllowedUtc < now) _nextAllowedUtc = now;
            wait = _nextAllowedUtc - now;
            _nextAllowedUtc += _minInterval;
        }
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
    }
}

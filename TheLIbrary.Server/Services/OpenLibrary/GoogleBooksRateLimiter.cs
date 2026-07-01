namespace TheLibrary.Server.Services.OpenLibrary;

// Raised when Google Books is unavailable because its quota is spent — either the
// daily-exhaustion latch is set, or Google returned a 429 / quota 403. The ISBN
// resolution paths treat it as "don't cache, retry later" (the row stays uncached,
// so it's re-attempted on a later run — the next day, once the daily quota resets).
public sealed class GoogleBooksQuotaExceededException : Exception
{
    public GoogleBooksQuotaExceededException(string? message = null)
        : base(message ?? "Google Books daily quota is exhausted — retrying later.") { }
}

// Paces Google Books calls under the per-minute cap (100/min/user) and latches
// "daily quota exhausted" once Google says so, so the rest of that day's calls are
// short-circuited instead of hammering a spent quota (1,000/day). The latch is keyed
// to the UTC date and clears automatically when the date rolls over — so lookups
// that couldn't run today are simply retried tomorrow. Singleton: shared across the
// content-scan pre-resolve and the resolve-isbns catch-up job.
public sealed class GoogleBooksRateLimiter
{
    // ~85 requests/min — comfortably under the 100/min/user ceiling with jitter room.
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(700);

    private readonly object _lock = new();
    private DateTime _nextAllowedUtc = DateTime.MinValue;
    private DateOnly? _exhaustedDay;

    // True while today's (UTC) quota is latched as exhausted.
    public bool IsExhaustedToday
    {
        get { lock (_lock) { return _exhaustedDay == DateOnly.FromDateTime(DateTime.UtcNow); } }
    }

    // Latch the daily quota as spent for the current UTC day (called when Google
    // returns a quota response). Auto-clears when the date rolls over.
    public void MarkExhausted()
    {
        lock (_lock) { _exhaustedDay = DateOnly.FromDateTime(DateTime.UtcNow); }
    }

    // Reserve the next call slot: throws GoogleBooksQuotaExceededException immediately
    // if the daily quota is latched (so no HTTP call is made), otherwise waits out the
    // per-minute spacing before returning.
    public async Task ReserveAsync(CancellationToken ct)
    {
        TimeSpan wait;
        lock (_lock)
        {
            if (_exhaustedDay == DateOnly.FromDateTime(DateTime.UtcNow))
                throw new GoogleBooksQuotaExceededException();
            var now = DateTime.UtcNow;
            if (_nextAllowedUtc < now) _nextAllowedUtc = now;
            wait = _nextAllowedUtc - now;
            _nextAllowedUtc += MinInterval;
        }
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
    }
}

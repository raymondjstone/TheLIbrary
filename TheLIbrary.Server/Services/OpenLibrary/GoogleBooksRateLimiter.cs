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
// short-circuited instead of hammering a spent quota (1,000/day). Singleton: shared
// across the content-scan pre-resolve, the resolve-isbns catch-up job, the Identified
// page's on-demand lookups, and the retry-isbn-misses job.
//
// The latch is keyed to the PACIFIC-TIME day, not UTC — Google Cloud API quotas
// (Books API included) reset at midnight Pacific Time, not UTC midnight. Using UTC
// here meant the latch cleared at 00:00 UTC while Google's real quota stayed spent
// until ~07:00-08:00 UTC (midnight Pacific, DST-dependent) — an up-to-8-hour window
// where every consumer thought it had a fresh quota, burned one real call
// "discovering" it didn't, and re-latched. A job scheduled inside that window (e.g.
// early morning UTC, specifically to catch a "fresh" day) would hit this on its very
// first attempt every single day and never make progress — confirmed live: a direct
// test call at 03:xx UTC returned Google's own 429 "Queries per day" quota-exceeded
// response despite the UTC date having just rolled over.
public sealed class GoogleBooksRateLimiter
{
    // ~85 requests/min — comfortably under the 100/min/user ceiling with jitter room.
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(700);

    // IANA id resolves on both Linux (with tzdata, which the runtime image installs)
    // and modern Windows. Falls back to a fixed UTC-8 (PST, never DST-adjusted) if the
    // tz database is somehow unavailable — wrong by an hour for roughly half the year,
    // but still far closer to Google's real reset than treating it as UTC midnight.
    private static readonly TimeZoneInfo PacificZone = ResolvePacificZone();

    private static TimeZoneInfo ResolvePacificZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"); }
        catch (Exception)
        {
            return TimeZoneInfo.CreateCustomTimeZone(
                "PacificFallback", TimeSpan.FromHours(-8), "Pacific (fallback, no tz database)", "Pacific (fallback)");
        }
    }

    // Public so other Google-quota-adjacent day-boundary bookkeeping (e.g. the
    // resolve-isbns catch-up job's own "deferred today" set) can share the same
    // reset cycle instead of each tracking a slightly-wrong UTC-day copy.
    public static DateOnly PacificToday()
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, PacificZone));

    private readonly object _lock = new();
    private DateTime _nextAllowedUtc = DateTime.MinValue;
    private DateOnly? _exhaustedDay;

    // True while today's (Pacific-time) quota is latched as exhausted.
    public bool IsExhaustedToday
    {
        get { lock (_lock) { return _exhaustedDay == PacificToday(); } }
    }

    // Latch the daily quota as spent for the current Pacific day (called when Google
    // returns a quota response). Auto-clears at the next Pacific-midnight rollover.
    public void MarkExhausted()
    {
        lock (_lock) { _exhaustedDay = PacificToday(); }
    }

    // Reserve the next call slot: throws GoogleBooksQuotaExceededException immediately
    // if the daily quota is latched (so no HTTP call is made), otherwise waits out the
    // per-minute spacing before returning.
    public async Task ReserveAsync(CancellationToken ct)
    {
        TimeSpan wait;
        lock (_lock)
        {
            if (_exhaustedDay == PacificToday())
                throw new GoogleBooksQuotaExceededException();
            var now = DateTime.UtcNow;
            if (_nextAllowedUtc < now) _nextAllowedUtc = now;
            wait = _nextAllowedUtc - now;
            _nextAllowedUtc += MinInterval;
        }
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
    }
}

namespace TheLibrary.Server.Services.OpenLibrary;

// Latches "quota exhausted" for the rest of the UTC day once ISBNdb signals its plan's
// request quota is spent (HTTP 429, or a 403 whose body cites a quota/limit reason) —
// so the rest of today's lookups short-circuit without an HTTP call instead of
// hammering a spent quota. Clears automatically at the UTC date rollover, matching
// ISBNdb's own quota reset. Singleton: shared by every ISBN resolution in the app
// (the resolve-isbns catch-up job and content-scan's inline pre-resolve both call
// through the same IsbndbFallbackProvider instance).
public sealed class IsbndbRateLimiter
{
    private readonly object _lock = new();
    private DateOnly? _exhaustedDay;

    // True while today's (UTC) quota is latched as exhausted.
    public bool IsExhaustedToday
    {
        get { lock (_lock) { return _exhaustedDay == DateOnly.FromDateTime(DateTime.UtcNow); } }
    }

    // Latch the quota as spent for the current UTC day. Auto-clears when the date
    // rolls over.
    public void MarkExhausted()
    {
        lock (_lock) { _exhaustedDay = DateOnly.FromDateTime(DateTime.UtcNow); }
    }
}

using System.ComponentModel.DataAnnotations;

namespace TheLibrary.Server.Data.Models;

// Tracks how many times an ISBN has been attempted and come back with NOTHING at all
// (no hit, no definitive miss from any source — every reachable source was
// rate/quota-capped) without ever landing a real IsbnResolutions row. Without this,
// such an ISBN is left permanently uncached and the catch-up job re-picks and
// re-attempts it forever, run after run, day after day. Once FailCount reaches
// Settings → *ISBN metadata fallbacks* → "give up after N failed attempts", the ISBN
// is instead cached as a permanent miss (same as any other unresolvable code) and this
// row is deleted — so it stops being retried at all. A row here is transient bookkeeping
// only; a successful resolution (of either kind) removes it.
public class IsbnResolutionAttempt
{
    // Normalized ISBN (IsbnResolution.IsbnKey) — the primary key.
    [Key]
    [MaxLength(20)]
    public string Isbn { get; set; } = "";

    public int FailCount { get; set; }
    public DateTime LastAttemptAt { get; set; }
}

using System.ComponentModel.DataAnnotations;

namespace TheLibrary.Server.Data.Models;

// Singleton (Id == 1) record of the reMarkable cloud pairing. The device
// token is long-lived and grants full cloud access, so it's the sensitive
// part; the user token is a short-lived JWT we cache between calls.
public class RemarkableAuth
{
    public int Id { get; set; }

    [MaxLength(4000)]
    public string DeviceToken { get; set; } = "";

    [MaxLength(4000)]
    public string? CachedUserToken { get; set; }

    public DateTime? UserTokenExpiresAt { get; set; }

    // Stable GUID sent on /device/new. Reused on subsequent pairings so the
    // cloud dashboard shows one device rather than proliferating entries.
    [MaxLength(100)]
    public string DeviceId { get; set; } = "";

    public DateTime ConnectedAt { get; set; }

    public DateTime? LastSentAt { get; set; }

    // JSON-serialised Dictionary<string, string> mapping canonical folder paths
    // ("Books", "Books/Author Name", "Books/Author Name/Series") to their
    // reMarkable cloud UUIDs. Avoids re-creating folders on every send without
    // needing a list API (which the v2 endpoint does not support).
    // Cleared on Disconnect so stale IDs don't accumulate across re-pairings.
    public string? FolderMapJson { get; set; }
}

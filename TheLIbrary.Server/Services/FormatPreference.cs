using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;

namespace TheLibrary.Server.Services;

// Single source of truth for "which ebook format is best". Used by the
// Duplicates page (best copy to KEEP) and the Archived Files page (best copy to
// RESTORE) so both rank formats identically.
public static class FormatPreference
{
    // Preference order — earlier = better. Matched against
    // Path.GetExtension(...).TrimStart('.').ToLowerInvariant().
    public static readonly string[] Default =
        ["epub", "azw3", "mobi", "pdf", "azw", "fb2", "lit", "cbz", "docx", "odt", "rtf", "prc", "pdb", "opf"];

    // The user's saved preference (AppSettings) if any, otherwise the default.
    public static async Task<string[]> LoadAsync(LibraryDbContext db, CancellationToken ct)
    {
        var raw = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.DuplicateFormatPreference)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        var parsed = string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(f => f.TrimStart('.').ToLowerInvariant())
                .Where(f => f.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        return parsed.Length > 0 ? parsed : Default;
    }

    // Lower rank = more preferred. Unknown/empty formats sort last.
    public static int Rank(string? format, IReadOnlyList<string> preference)
    {
        if (string.IsNullOrWhiteSpace(format)) return int.MaxValue;
        for (var idx = 0; idx < preference.Count; idx++)
            if (string.Equals(preference[idx], format, StringComparison.OrdinalIgnoreCase))
                return idx;
        return preference.Count + 100;
    }
}

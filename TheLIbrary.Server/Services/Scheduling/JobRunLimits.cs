using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;

namespace TheLibrary.Server.Services.Scheduling;

// Reads a per-run cap from AppSettings, falling back to the calling job's own
// default when the setting is unset, blank or not a positive integer. Keeps the
// "Background job run limits" Settings section and every capped job in agreement
// without each one re-implementing the same parse-or-default dance.
public static class JobRunLimits
{
    public static async Task<int> GetAsync(LibraryDbContext db, string key, int fallback, CancellationToken ct)
    {
        var raw = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        return int.TryParse(raw, out var n) && n > 0 ? n : fallback;
    }
}

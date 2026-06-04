using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;

namespace TheLibrary.Server.Services.OpenLibrary;

// Holds the currently-effective cover-cache directory in memory so the serving
// controller and the cache job don't hit the DB per request. Updated at startup
// and whenever the Settings path changes.
public sealed class CoverCacheState
{
    private volatile string? _directory;
    public string? Directory { get => _directory; set => _directory = value; }
}

public static class CoverCacheResolver
{
    // The effective directory: the saved setting if present, otherwise the
    // default derived from the (first enabled) library location.
    public static async Task<string> ResolveAsync(LibraryDbContext db, IWebHostEnvironment env, CancellationToken ct = default)
    {
        var stored = (await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.CachedCoversFolder)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct))?.Trim();
        if (!string.IsNullOrWhiteSpace(stored)) return Normalize(stored);

        var libraryPath = await db.LibraryLocations.AsNoTracking()
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .FirstOrDefaultAsync(ct);
        return DefaultFor(libraryPath, env);
    }

    // Default = the library location's parent with a "cached-covers" leaf, so it
    // sits on the same writable mount (e.g. "/Books/Collection" -> "/Books/
    // cached-covers"). Falls back to wwwroot only when no library is configured.
    public static string DefaultFor(string? libraryPath, IWebHostEnvironment env)
    {
        if (!string.IsNullOrWhiteSpace(libraryPath))
        {
            var norm = Normalize(libraryPath);
            var slash = norm.LastIndexOf('/');
            var parent = slash > 0 ? norm[..slash] : norm;
            return $"{parent}/cached-covers";
        }
        return Normalize(Path.Combine(
            env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot"), "cached-covers"));
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');
}

using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;

namespace TheLibrary.Server.Services.Calibre;

// Resolves the effective location(s) of the __unknown quarantine bucket.
// When AppSettings["UnknownFolder"] is set, ALL quarantine operations use
// that single path; otherwise each library location keeps its own
// <root>/__unknown subfolder (the historical default).
public static class UnknownFolderResolver
{
    // The configured override path, or null when the user hasn't set one.
    public static async Task<string?> GetCustomPathAsync(LibraryDbContext db, CancellationToken ct)
    {
        var row = await db.AppSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == AppSettingKeys.UnknownFolder, ct);
        var value = row?.Value?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    // Where new quarantine items coming from `sourceLocationPath` should land.
    // The custom path takes precedence; otherwise fall back to the per-location
    // default.
    public static async Task<string> GetDestinationRootAsync(
        LibraryDbContext db,
        string sourceLocationPath,
        CancellationToken ct)
    {
        var custom = await GetCustomPathAsync(db, ct);
        return custom ?? Path.Combine(sourceLocationPath, CalibreScanner.UnknownAuthorFolder);
    }

    // All roots to scan when enumerating quarantine contents. With a custom
    // path set, returns just that path; otherwise returns one path per enabled
    // library location.
    public static async Task<IReadOnlyList<string>> GetSourceRootsAsync(
        LibraryDbContext db,
        IReadOnlyList<string> libraryLocations,
        CancellationToken ct)
    {
        var custom = await GetCustomPathAsync(db, ct);
        if (custom is not null) return new[] { custom };
        return libraryLocations
            .Select(l => Path.Combine(l, CalibreScanner.UnknownAuthorFolder))
            .ToList();
    }

    // Given an arbitrary `rootPath` (as returned by the listing API), produce
    // the actual on-disk quarantine root. When the custom path is set, the
    // rootPath argument is ignored and the custom path is used unconditionally.
    public static async Task<string> ResolveBucketRootAsync(
        LibraryDbContext db,
        string rootPath,
        CancellationToken ct)
    {
        var custom = await GetCustomPathAsync(db, ct);
        return custom ?? Path.Combine(rootPath, CalibreScanner.UnknownAuthorFolder);
    }
}

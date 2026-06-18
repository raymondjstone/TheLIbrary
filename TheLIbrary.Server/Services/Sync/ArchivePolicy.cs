using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;

namespace TheLibrary.Server.Services.Sync;

// Single source of truth for "is this file in the archive holding folder?".
//
// Archived files are INERT by policy: once a copy is moved to the archive folder
// (via the Duplicates page), no job may scan, re-index, move, re-link, or delete
// it until the user explicitly restores it from the Archived Files page. Every
// job that walks LocalBookFiles must exclude archived rows so a previously
// archived duplicate can never be dragged back into a live author/series folder
// (which is what made "cleared" duplicates reappear every few days).
//
// The configured DedupeArchiveFolder is either a bare leaf name ("__archive",
// matched as a path component) or a full absolute path ("/Books/TheLibrary_Archive",
// matched as a prefix). Stored paths are always forward-slash (Linux mount), so
// matching is on '/' explicitly rather than Path.DirectorySeparatorChar (which is
// '\' when this code runs on the Windows dev box and would never match).
public static class ArchivePolicy
{
    public const string DefaultLeaf = "__archive";

    public static async Task<string> LoadLeafAsync(LibraryDbContext db, CancellationToken ct)
    {
        var raw = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.DedupeArchiveFolder)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        return Normalize(raw);
    }

    public static string Normalize(string? raw)
        => (string.IsNullOrWhiteSpace(raw) ? DefaultLeaf : raw.Trim()).Replace('\\', '/').TrimEnd('/');

    // The folder NAME the scanner must refuse to descend into. When the archive is
    // configured as an absolute path this is its final segment; the scanner ignores
    // top-level folders by name, and the default archive lives at <root>/__archive.
    public static string FolderName(string leaf)
    {
        var n = Normalize(leaf);
        var slash = n.LastIndexOf('/');
        return slash >= 0 ? n[(slash + 1)..] : n;
    }

    // Absolute archive directory paths the scanner must not descend into. A bare
    // leaf ("__archive") lives at <root>/<leaf> for every library root; an absolute
    // path ("/Books/TheLibrary_Archive") is the directory itself, wherever it sits.
    // Path-based (not name-based) so an archive nested deeper than a root's direct
    // child is still excluded.
    public static IEnumerable<string> AbsoluteDirs(string leaf, IEnumerable<string> roots)
    {
        var n = Normalize(leaf);
        if (n.Contains('/'))
        {
            yield return n;
            yield break;
        }
        foreach (var r in roots)
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            yield return r.Replace('\\', '/').TrimEnd('/') + "/" + n;
        }
    }

    // EF-translatable predicate: rows NOT under the archive folder.
    public static Expression<Func<LocalBookFile, bool>> NotUnder(string leaf)
    {
        var n = Normalize(leaf);
        if (n.Contains('/'))
        {
            var prefix = n + "/";
            return f => !f.FullPath.StartsWith(prefix);
        }
        var segment = "/" + n + "/";
        return f => !f.FullPath.Contains(segment);
    }

    // In-memory check for a single path (use when filtering a materialised list).
    public static bool IsUnder(string? fullPath, string leaf)
    {
        if (string.IsNullOrEmpty(fullPath)) return false;
        var p = fullPath.Replace('\\', '/');
        var n = Normalize(leaf);
        return n.Contains('/')
            ? p.StartsWith(n + "/", StringComparison.OrdinalIgnoreCase)
            : p.Contains("/" + n + "/", StringComparison.OrdinalIgnoreCase);
    }
}

using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.IO;

namespace TheLibrary.Server.Services.Sync;

// Shared archive-move primitive used by every AUTOMATED "archive this extra
// copy" path (DuplicateAutoArchiveService, and the content-verified variant) —
// the same forward-slash / cross-mount-safe move logic as BooksController's
// manual "Archive extras" action, factored out so it's implemented exactly
// once. Never Path.Combine (stored paths are always forward-slash on the Linux
// mount); verifies the source is actually gone before repointing the row, so a
// CIFS copy+delete race can't resurrect the original as a fresh duplicate.
public static class DuplicateFileArchiver
{
    // Moves a file into the archive folder, preserving its library-relative
    // subpath. Returns true on success; false (row left as-is) when the source
    // can't be resolved/removed.
    public static async Task<bool> ArchiveAsync(
        LocalBookFile file, string archiveLeaf, IReadOnlyList<string> locations,
        IFileSystem fs, ILogger log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(file.FullPath)) return false;
        var location = locations.FirstOrDefault(l =>
            file.FullPath.StartsWith(l.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
        if (location is null) return false;

        var libRoot = location.Replace('\\', '/').TrimEnd('/');
        var relative = file.FullPath.Replace('\\', '/')[libRoot.Length..].TrimStart('/');
        var destBase = (archiveLeaf.Contains('/') || archiveLeaf.Contains('\\'))
            ? archiveLeaf.Replace('\\', '/').TrimEnd('/')
            : $"{libRoot}/{archiveLeaf}";
        var destPath = $"{destBase}/{relative}";
        var destDir = destPath[..destPath.LastIndexOf('/')];

        try
        {
            if (!await fs.FileExistsAsync(file.FullPath, ct)) return false;
            await fs.CreateDirectoryAsync(destDir, ct);
            var final = (await UniqueFileAsync(destPath, fs, ct)).Replace('\\', '/');
            await fs.MoveFileAsync(file.FullPath, final, overwrite: false, ct);
            if (await fs.FileExistsAsync(file.FullPath, ct))
            {
                log.LogWarning("Duplicate archive: could not remove source {Path} — left as-is", file.FullPath);
                return false; // never repoint while the live original survives
            }
            file.FullPath = final;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Duplicate archive: failed to archive {Path}", file.FullPath);
            return false;
        }
    }

    public static async Task<string> UniqueFileAsync(string desired, IFileSystem fs, CancellationToken ct)
    {
        if (!await fs.FileExistsAsync(desired, ct) && !await fs.DirectoryExistsAsync(desired, ct)) return desired;
        var dir = desired[..desired.LastIndexOf('/')];
        var name = desired[(desired.LastIndexOf('/') + 1)..];
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 2; i < 1000; i++)
        {
            var next = $"{dir}/{stem}_{i}{ext}";
            if (!await fs.FileExistsAsync(next, ct) && !await fs.DirectoryExistsAsync(next, ct)) return next;
        }
        return $"{dir}/{stem}_{DateTime.UtcNow:yyyyMMddHHmmss}{ext}";
    }
}

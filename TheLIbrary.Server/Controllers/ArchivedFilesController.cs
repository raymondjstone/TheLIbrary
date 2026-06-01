using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services;
using TheLibrary.Server.Services.IO;

namespace TheLibrary.Server.Controllers;

[ApiController]
[Route("api/archived-files")]
public class ArchivedFilesController : ControllerBase
{
    private readonly LibraryDbContext _db;
    private readonly IFileSystem _fs;

    public ArchivedFilesController(LibraryDbContext db, IFileSystem fs)
    {
        _db = db;
        _fs = fs;
    }

    public sealed record ArchivedFileItem(
        int Id,
        string Path,
        string? Format,
        long SizeBytes);

    // One book's worth of archived files, mirroring the Duplicates page shape so
    // the client can render and act on them identically. RecommendedFormat is the
    // best copy to restore, using the same ranking as the dedupe "keep" choice.
    public sealed record ArchivedGroup(
        int? BookId,
        string Title,
        int? AuthorId,
        string AuthorName,
        IReadOnlyList<ArchivedFileItem> Files,
        string? RecommendedFormat);

    public sealed record RestoreResult(int Restored, IReadOnlyList<string> Warnings);

    private static string GetArchiveLeaf(string? val)
        => string.IsNullOrWhiteSpace(val) ? "__archive" : val.Trim();

    // Predicate that matches files living under the archive folder. Stored paths
    // are always forward-slash (the library is on a Linux mount), so we match on
    // '/' explicitly rather than Path.DirectorySeparatorChar — which is '\' when
    // this code runs on a Windows host and would never match the stored paths.
    // The setting is either a simple leaf name ("__archive", matched as a path
    // component) or a full absolute path ("/Books/TheLibrary_Archive", matched as
    // a prefix). Anything containing a separator is treated as the latter.
    private static System.Linq.Expressions.Expression<Func<LocalBookFile, bool>> ArchiveMatch(string archiveLeaf)
    {
        var leaf = archiveLeaf.Replace('\\', '/').TrimEnd('/');
        if (leaf.Contains('/'))
        {
            var prefix = leaf + "/";
            return f => f.FullPath.StartsWith(prefix);
        }
        var segment = "/" + leaf + "/";
        return f => f.FullPath.Contains(segment);
    }

    /// <summary>
    /// Returns archived LocalBookFiles grouped by book (mirroring the Duplicates
    /// page), each with the recommended copy to restore. GET /api/archived-files
    /// </summary>
    [HttpGet]
    public async Task<IReadOnlyList<ArchivedGroup>> GetArchivedFiles(CancellationToken ct = default)
    {
        var archiveSetting = await _db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == AppSettingKeys.DedupeArchiveFolder, ct);
        var archiveLeaf = GetArchiveLeaf(archiveSetting?.Value);
        var preference = await FormatPreference.LoadAsync(_db, ct);

        var rows = await _db.LocalBookFiles.AsNoTracking()
            .Where(ArchiveMatch(archiveLeaf))
            .Select(f => new
            {
                f.Id,
                f.FullPath,
                f.BookId,
                f.AuthorFolder,
                f.TitleFolder,
                f.SizeBytes,
                BookTitle = f.Book != null ? f.Book.Title : null,
                BookAuthorId = f.Book != null ? (int?)f.Book.AuthorId : null,
                BookAuthorName = f.Book != null ? f.Book.Author.Name : null,
            })
            .ToListAsync(ct);

        // Group by the linked book when there is one; otherwise fall back to the
        // author + title folder so loose archived files still cluster sensibly.
        var groups = rows
            .GroupBy(r => r.BookId.HasValue
                ? $"b:{r.BookId.Value}"
                : $"f:{r.AuthorFolder}{r.TitleFolder}")
            .Select(g =>
            {
                var first = g.First();
                var files = g
                    .Select(r => new ArchivedFileItem(r.Id, r.FullPath, FormatOf(r.FullPath), r.SizeBytes))
                    .OrderBy(x => FormatPreference.Rank(x.Format, preference))
                    .ToList();
                var recommended = files
                    .Select(x => x.Format)
                    .Where(fmt => fmt is not null)
                    .OrderBy(fmt => FormatPreference.Rank(fmt, preference))
                    .FirstOrDefault();
                var title = first.BookId.HasValue
                    ? (first.BookTitle ?? first.TitleFolder ?? "(untitled)")
                    : (first.TitleFolder ?? "(untitled)");
                var authorName = (first.BookId.HasValue ? first.BookAuthorName : null)
                    ?? first.AuthorFolder ?? "";
                return new ArchivedGroup(
                    first.BookId, title, first.BookAuthorId, authorName, files, recommended);
            })
            .OrderBy(grp => grp.AuthorName).ThenBy(grp => grp.Title)
            .ToList();

        return groups;
    }

    /// <summary>
    /// Restores one or more archived files to the incoming folder.
    /// POST /api/archived-files/restore  { "fileIds": [1, 2, 3] }
    /// </summary>
    [HttpPost("restore")]
    public async Task<ActionResult<RestoreResult>> RestoreToIncoming(
        [FromBody] RestoreRequest body,
        CancellationToken ct)
    {
        if (body.FileIds is null || body.FileIds.Count == 0)
            return BadRequest(new { error = "At least one file id is required." });

        var incomingSetting = await _db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == AppSettingKeys.IncomingFolder, ct);
        var incomingPath = incomingSetting?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(incomingPath))
            return BadRequest(new { error = "Incoming folder is not configured. Set it on the Settings page first." });

        if (!Directory.Exists(incomingPath))
        {
            try { Directory.CreateDirectory(incomingPath); }
            catch (Exception ex) { return BadRequest(new { error = $"Cannot access incoming folder: {ex.Message}" }); }
        }

        var files = await _db.LocalBookFiles
            .Where(f => body.FileIds.Contains(f.Id))
            .ToListAsync(ct);

        if (files.Count == 0) return NotFound(new { error = "No matching files found." });

        var warnings = new List<string>();
        var restored = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(file.FullPath))
            {
                warnings.Add($"#{file.Id}: no path recorded, skipped.");
                continue;
            }

            var isDir = await _fs.DirectoryExistsAsync(file.FullPath, ct);
            var isFile = !isDir && await _fs.FileExistsAsync(file.FullPath, ct);

            if (!isDir && !isFile)
            {
                warnings.Add($"#{file.Id}: path no longer exists on disk, skipped.");
                continue;
            }

            var leaf = isDir
                ? Path.GetFileName(file.FullPath.TrimEnd(Path.DirectorySeparatorChar))
                : Path.GetFileName(file.FullPath);

            var dest = Path.Combine(incomingPath, leaf);
            // Avoid overwriting an existing item with the same name.
            dest = UniqueDestination(incomingPath, leaf, isDir);

            try
            {
                if (isDir)
                {
                    await _fs.MoveDirectoryAsync(file.FullPath, dest, ct);
                }
                else
                {
                    await _fs.MoveFileAsync(file.FullPath, dest, overwrite: false, ct);
                }
                file.FullPath = dest;
                restored++;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                warnings.Add($"#{file.Id}: {ex.Message}");
            }
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new RestoreResult(restored, warnings));
    }

    public sealed record RestoreRequest(IReadOnlyList<int> FileIds);

    private static string UniqueDestination(string parent, string leaf, bool isDir)
    {
        var candidate = Path.Combine(parent, leaf);
        if (isDir ? !Directory.Exists(candidate) : !System.IO.File.Exists(candidate))
            return candidate;
        var name = isDir ? leaf : Path.GetFileNameWithoutExtension(leaf);
        var ext = isDir ? "" : Path.GetExtension(leaf);
        for (var n = 1; ; n++)
        {
            candidate = Path.Combine(parent, $"{name}_{n}{ext}");
            if (isDir ? !Directory.Exists(candidate) : !System.IO.File.Exists(candidate))
                return candidate;
        }
    }

    private static string? FormatOf(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return null;
        var ext = Path.GetExtension(fullPath).TrimStart('.').ToLowerInvariant();
        if (!string.IsNullOrEmpty(ext)) return ext;
        if (!Directory.Exists(fullPath)) return null;
        try
        {
            return Directory.EnumerateFiles(fullPath)
                .Select(f => Path.GetExtension(f).TrimStart('.').ToLowerInvariant())
                .Where(e => !string.IsNullOrEmpty(e))
                .OrderBy(e => FormatPreference.Rank(e, FormatPreference.Default))
                .FirstOrDefault();
        }
        catch { return null; }
    }
}

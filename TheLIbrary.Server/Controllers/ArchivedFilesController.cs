using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
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

    public sealed record ArchivedFileDto(
        int Id,
        string FullPath,
        string? AuthorFolder,
        string? TitleFolder,
        string? Format,
        long SizeBytes);

    public sealed record ArchivedFilesPageDto(
        int TotalCount,
        int Page,
        int PageSize,
        IReadOnlyList<ArchivedFileDto> Items);

    public sealed record RestoreResult(int Restored, IReadOnlyList<string> Warnings);

    private static string GetArchiveLeaf(string? val)
        => string.IsNullOrWhiteSpace(val) ? "__archive" : val.Trim();

    /// <summary>
    /// Returns paged list of LocalBookFiles that live inside any of the
    /// configured library roots under the archive leaf folder.
    /// GET /api/archived-files?page=0&pageSize=25
    /// </summary>
    [HttpGet]
    public async Task<ArchivedFilesPageDto> GetArchivedFiles(
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        page = Math.Max(0, page);

        var archiveSetting = await _db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == AppSettingKeys.DedupeArchiveFolder, ct);
        var archiveLeaf = GetArchiveLeaf(archiveSetting?.Value);

        // Segment separator ensures we match the folder name as a path component
        // and not as a substring inside another folder name (e.g. "__archive2").
        var sep = Path.DirectorySeparatorChar;
        var archiveSegment = $"{sep}{archiveLeaf}{sep}";

        var query = _db.LocalBookFiles
            .AsNoTracking()
            .Where(f => f.FullPath.Contains(archiveSegment));

        var total = await query.CountAsync(ct);

        var rows = await query
            .OrderBy(f => f.AuthorFolder)
            .ThenBy(f => f.TitleFolder)
            .Skip(page * pageSize)
            .Take(pageSize)
            .Select(f => new ArchivedFileDto(
                f.Id,
                f.FullPath,
                f.AuthorFolder,
                f.TitleFolder,
                null, // format resolved below
                f.SizeBytes))
            .ToListAsync(ct);

        // Resolve file format from extension or Calibre folder scan.
        var items = rows.Select(r => r with { Format = FormatOf(r.FullPath) }).ToList();

        return new ArchivedFilesPageDto(total, page, pageSize, items);
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
                .OrderBy(e => PreferenceRank(e))
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static readonly string[] _formatPref =
        ["epub", "pdf", "azw3", "mobi", "azw", "fb2", "lit", "cbz", "docx", "odt", "rtf"];

    private static int PreferenceRank(string ext)
    {
        var idx = Array.IndexOf(_formatPref, ext);
        return idx < 0 ? 999 : idx;
    }
}

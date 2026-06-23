using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.Incoming;
using TheLibrary.Server.Services.IO;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Services.Scheduling;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Controllers;

// Untracked / quarantine surface (unclaimed library folders, the __unknown
// bucket, and the folder browser/preview/match flow). Split out of the main
// AuthorsController file for navigability; same partial class, so it keeps
// sharing that file's private helpers (FormatsInFolder, ResolveLocalCopy,
// FindLibraryRootForPath, ResolveUntrackedSourcePathAsync, …) unchanged.
public partial class AuthorsController
{
    public sealed record UnclaimedFolder(
        string AuthorFolder, int FileCount, IReadOnlyList<string> RootPaths, IReadOnlyList<string> Formats,
        // Integrity-check tally across this folder's files (see BookIntegrityService):
        // Ok = passed, Damaged = failed, Unchecked = never run yet.
        int IntegrityOk, int IntegrityDamaged, int IntegrityUnchecked,
        // Most-recent file modified time in the folder — lets the UI surface
        // newly-arrived items at the top (the default sort).
        DateTime ModifiedAt = default);

    public sealed record UntrackedFolderEntry(
        string Name,
        string RelativePath,
        bool IsDirectory,
        string SearchQuery);

    public sealed record UntrackedFolderContents(
        string Bucket,
        string Folder,
        string RootPath,
        string CurrentPath,
        string? ParentPath,
        bool RecursiveFilesOnly,
        IReadOnlyList<UntrackedFolderEntry> Entries);

    public sealed record MatchUntrackedOpenLibraryRequest(
        string Bucket,
        string Folder,
        string RootPath,
        string? RelativePath,
        string? WorkKey,
        string? Title,
        int? FirstPublishYear,
        int? CoverId,
        string? Authors,
        string? PrimaryAuthorKey,
        string? PrimaryAuthorName,
        // When true, file the book under the catch-all "Unknown Author" instead of
        // resolving the work's author — for files whose author can't be trusted.
        bool UnknownAuthor = false);

    // Library author folders that don't match any tracked author.
    [HttpGet("~/api/unclaimed")]
    public async Task<IReadOnlyList<UnclaimedFolder>> Unclaimed(CancellationToken ct)
    {
        var locations = await _db.LibraryLocations
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);

        var rows = await _db.LocalBookFiles.AsNoTracking()
            .Where(f => f.AuthorId == null)
            .ToListAsync(ct);

        return rows
            .GroupBy(f => f.AuthorFolder)
            .Select(g => new UnclaimedFolder(
                g.Key,
                g.Count(),
                g.Select(f => FindLibraryRootForPath(f.FullPath, locations))
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Cast<string>()
                    .ToList(),
                g.SelectMany(f => FormatsInFolder(f.FullPath))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x)
                    .ToList(),
                g.Count(f => f.IntegrityOk == true),
                g.Count(f => f.IntegrityOk == false),
                g.Count(f => f.IntegrityOk == null),
                g.Max(f => f.ModifiedAt)))
            // Newest activity first so freshly-arrived folders surface at the top.
            .OrderByDescending(u => u.ModifiedAt)
            .ThenBy(u => u.AuthorFolder)
            .ToList();
    }

    [HttpGet("~/api/untracked/contents")]
    public async Task<ActionResult<UntrackedFolderContents>> GetUntrackedContents(
        [FromQuery] string bucket,
        [FromQuery] string folder,
        [FromQuery] string rootPath,
        [FromQuery] string? path,
        [FromQuery] bool recursiveFilesOnly,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(rootPath))
            return BadRequest(new { error = "bucket, folder, and rootPath are required" });

        var sourcePath = await ResolveUntrackedSourcePathAsync(bucket, folder, rootPath, path, ct);
        if (sourcePath is null)
            return NotFound(new { error = "Folder not found" });
        if (!Directory.Exists(sourcePath))
            return BadRequest(new { error = "Only folders can be drilled into" });

        var relativePath = NormalizeRelativePath(path);
        var parentPath = string.IsNullOrWhiteSpace(relativePath)
            ? null
            : NormalizeRelativePath(Path.GetDirectoryName(relativePath)?.Replace('\\', '/'));

        List<UntrackedFolderEntry> entries;
        if (recursiveFilesOnly)
        {
            entries = Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories)
                .Where(p => CalibreScanner.EbookExtensions.Contains(Path.GetExtension(p))
                            || CalibreScanner.ArchiveExtensions.Contains(Path.GetExtension(p)))
                .Select(p => Path.GetRelativePath(sourcePath, p).Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => new UntrackedFolderEntry(
                    Path.GetFileName(p),
                    CombineRelativePath(relativePath, p),
                    false,
                    Path.GetFileNameWithoutExtension(p)))
                .ToList();
        }
        else
        {
            entries = Directory.EnumerateFileSystemEntries(sourcePath)
                .Where(p => Directory.Exists(p)
                            || CalibreScanner.EbookExtensions.Contains(Path.GetExtension(p))
                            || CalibreScanner.ArchiveExtensions.Contains(Path.GetExtension(p)))
                .Select(p => new
                {
                    Path = p,
                    Name = Path.GetFileName(p),
                    IsDirectory = Directory.Exists(p)
                })
                .OrderByDescending(x => x.IsDirectory)
                .ThenBy(x => x.Name)
                .Select(x => new UntrackedFolderEntry(
                    x.Name,
                    CombineRelativePath(relativePath, x.Name),
                    x.IsDirectory,
                    x.IsDirectory ? x.Name : Path.GetFileNameWithoutExtension(x.Name)))
                .ToList();
        }

        return Ok(new UntrackedFolderContents(bucket.Trim(), folder.Trim(), rootPath.Trim(), relativePath, parentPath, recursiveFilesOnly, entries));
    }

    // In-browser preview for files that live under unclaimed/__unknown. The
    // file isn't in LocalBookFiles yet, so the regular /api/files/{id}/preview
    // endpoint won't find it — this one resolves via the same untracked path
    // resolver and streams the bytes with an `inline` disposition.
    [HttpGet("~/api/untracked/preview")]
    public async Task<IActionResult> PreviewUntracked(
        [FromQuery] string bucket,
        [FromQuery] string folder,
        [FromQuery] string rootPath,
        [FromQuery] string? path,
        [FromQuery] string format,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(format))
            return BadRequest(new { error = "format query parameter is required" });

        var sourcePath = await ResolveUntrackedSourcePathAsync(bucket, folder, rootPath, path, ct);
        if (sourcePath is null) return NotFound(new { error = "Selected path not found" });

        if (!System.IO.File.Exists(sourcePath))
            return NotFound(new { error = "Only files can be previewed" });

        var ext = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
        var supportedConversions = new[] { "mobi", "azw", "azw3", "fb2", "lit", "docx", "odt", "cbz", "zip" };
        var isConvertible = supportedConversions.Contains(ext);

        if (!isConvertible && !string.Equals(ext, format, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = $"File extension '.{ext}' does not match requested format '.{format}'" });

        string? contentType = "application/octet-stream";
        if (isConvertible || !Services.Sync.FilePreviewResolver.SupportedFormats.TryGetValue(format, out contentType))
        {
            if (isConvertible && (format.Equals("epub", StringComparison.OrdinalIgnoreCase) || format.Equals(ext, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var convertedPath = await _converter.ConvertToEpubAsync(sourcePath, ct);
                    if (System.IO.File.Exists(convertedPath))
                    {
                        var bytes = await System.IO.File.ReadAllBytesAsync(convertedPath, ct);
                        try { System.IO.File.Delete(convertedPath); } catch { /* best effort */ }
                        Response.Headers["Content-Disposition"] = $"inline; filename=\"{Path.GetFileNameWithoutExtension(sourcePath)}.epub\"";
                        return File(bytes, "application/epub+zip");
                    }
                }
                catch (Exception ex)
                {
                    return StatusCode(500, new { error = $"On-the-fly conversion to EPUB failed: {ex.Message}" });
                }
            }

            if (!isConvertible)
            {
                return StatusCode(415, new { error = $"Preview not supported for '.{format}'. Supported: epub, pdf, txt, cbz, zip." });
            }
        }

        // Build the allowed-roots list: every enabled library location PLUS
        // the custom __unknown path when one is set (it may live outside the
        // library locations entirely).
        var roots = await _db.LibraryLocations.AsNoTracking()
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);
        var customUnknown = await UnknownFolderResolver.GetCustomPathAsync(_db, ct);
        var allowedRoots = customUnknown is null
            ? (IReadOnlyList<string>)roots
            : roots.Append(customUnknown).ToList();

        if (!Services.Sync.FilePreviewResolver.IsInsideAnyRoot(sourcePath, allowedRoots))
            return StatusCode(403, new { error = "Refusing to serve a file outside enabled library locations" });

        // RTF: convert to plain text on the fly (RtfPipe) for the txt pane.
        if (string.Equals(format, "rtf", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var rtf = await System.IO.File.ReadAllTextAsync(sourcePath, ct);
                return Content(Services.Calibre.RtfTextExtractor.ExtractText(rtf), "text/plain; charset=utf-8");
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = $"Could not read RTF: {ex.Message}" });
            }
        }

        var safeName = Path.GetFileName(sourcePath).Replace("\"", "");
        Response.Headers["Content-Disposition"] = $"inline; filename=\"{safeName}\"";
        // contentType can be null when a supported format had no MIME mapping;
        // fall back to a generic binary type so PhysicalFile never gets null.
        return PhysicalFile(sourcePath, contentType ?? "application/octet-stream", enableRangeProcessing: true);
    }

    [HttpPost("~/api/untracked/match-openlibrary")]
    public async Task<ActionResult<object>> MatchUntrackedToOpenLibrary(
        [FromBody] MatchUntrackedOpenLibraryRequest body,
        CancellationToken ct)
    {
        var sourcePath = await ResolveUntrackedSourcePathAsync(body.Bucket, body.Folder, body.RootPath, body.RelativePath, ct);
        if (sourcePath is null)
            return NotFound(new { error = "Selected path not found" });

        var targetAuthor = body.UnknownAuthor
            ? await _assigner.EnsureUnknownAuthorAsync(ct)
            : await ResolveTargetAuthorAsync(null, body.PrimaryAuthorKey, body.PrimaryAuthorName, body.Authors, ct);
        if (targetAuthor is null)
            return BadRequest(new { error = "Could not determine the OpenLibrary author for this work" });

        var add = await EnsureOpenLibraryBookAsync(
            targetAuthor.Id,
            body.WorkKey,
            body.Title,
            body.FirstPublishYear,
            body.CoverId,
            owned: false,
            ct);
        if (add.Error is not null)
            return BadRequest(new { error = add.Error });

        var existing = await _db.LocalBookFiles.FirstOrDefaultAsync(f => f.FullPath == sourcePath, ct);
        var file = existing ?? new LocalBookFile();
        if (existing is null)
            _db.LocalBookFiles.Add(file);

        // The author folder must be created under a real library location, NOT
        // under body.RootPath — for the __unknown bucket with a custom path the
        // request's RootPath IS the __unknown directory, so filing relative to it
        // would bury the book in a subfolder inside __unknown instead of moving it
        // out to the author's folder in the library.
        var (destRoot, destRootError) = await ResolveDestinationRootAsync(sourcePath, ct);
        if (destRoot is null)
            return BadRequest(new { error = destRootError });

        var finalPath = await MoveUntrackedPathToAuthorFolderAsync(
            sourcePath,
            destRoot,
            NormalizeRelativePath(body.RelativePath),
            targetAuthor,
            ct);

        file.AuthorId = targetAuthor.Id;
        file.BookId = add.Book!.Id;
        file.ManuallyUnmatched = false;
        file.AuthorFolder = targetAuthor.CalibreFolderName ?? targetAuthor.Name;
        file.TitleFolder = Directory.Exists(finalPath)
            ? Path.GetFileName(finalPath)
            : Path.GetFileNameWithoutExtension(finalPath);
        file.FullPath = finalPath;
        file.NormalizedTitle = TitleNormalizer.Normalize(file.TitleFolder);
        file.ResetIntegrity(); // moved into the author folder — re-check it there

        await _db.SaveChangesAsync(ct);
        return Ok(new { authorId = targetAuthor.Id, bookId = add.Book.Id, fullPath = finalPath });
    }

    [HttpDelete("~/api/untracked")]
    public async Task<IActionResult> DeleteUntrackedPath(
        [FromQuery] string bucket,
        [FromQuery] string folder,
        [FromQuery] string rootPath,
        [FromQuery] string? path,
        CancellationToken ct)
    {
        var sourcePath = await ResolveUntrackedSourcePathAsync(bucket, folder, rootPath, path, ct);
        if (sourcePath is null)
            return NotFound(new { error = "Selected path not found" });

        var isDirectory = _fs.DirectoryExists(sourcePath);
        var isFile = _fs.FileExists(sourcePath);
        if (!isDirectory && !isFile)
            return NotFound(new { error = "Selected path no longer exists on disk" });

        if (isDirectory)
            _fs.DeleteDirectory(sourcePath, recursive: true);
        else
            _fs.DeleteFile(sourcePath);

        var normalizedSource = sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var directParent = Path.GetDirectoryName(normalizedSource);
        var prefix1 = normalizedSource + Path.DirectorySeparatorChar;
        var prefix2 = normalizedSource + Path.AltDirectorySeparatorChar;

        var staleRows = await _db.LocalBookFiles
            .Where(f => f.FullPath == normalizedSource
                     || (isDirectory && (f.FullPath.StartsWith(prefix1) || f.FullPath.StartsWith(prefix2)))
                     || (!isDirectory && directParent != null && f.FullPath == directParent))
            .ToListAsync(ct);
        if (staleRows.Count > 0)
            _db.LocalBookFiles.RemoveRange(staleRows);

        var bucketRoot = await ResolveUntrackedSourcePathAsync(bucket, folder, rootPath, null, ct);
        if (!string.IsNullOrWhiteSpace(bucketRoot))
            await PruneEmptyParentsAsync(Path.GetDirectoryName(normalizedSource), bucketRoot, ct);

        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // Moves all files for an untracked folder back to incoming, deletes the
    // (now-empty) author folder from the library, and removes the DB rows.
    public sealed record AssignUnknownResult(int Mapped, IReadOnlyList<string> Warnings);

    // Files every file in an unclaimed library folder under the catch-all
    // "Unknown Author" (created on demand): each file moves into that author's
    // folder and is linked to it (unmatched — a book can still be matched later).
    // For folders whose real author can't be determined.
    [HttpPost("~/api/unclaimed/assign-unknown")]
    public async Task<ActionResult<AssignUnknownResult>> AssignUnclaimedToUnknown(
        [FromQuery] string folder, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return BadRequest(new { error = "folder is required" });

        var files = await _db.LocalBookFiles
            .Where(f => f.AuthorFolder == folder && f.AuthorId == null)
            .ToListAsync(ct);
        if (files.Count == 0)
            return NotFound(new { error = $"No unclaimed files found for folder '{folder}'." });

        var unknown = await _assigner.EnsureUnknownAuthorAsync(ct);
        var warnings = new List<string>();
        var mapped = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(file.FullPath)) continue;
            var (root, err) = await _assigner.ResolveDestinationRootAsync(file.FullPath, ct);
            if (root is null) { warnings.Add($"{Path.GetFileName(file.FullPath)}: {err}"); continue; }
            try
            {
                var finalPath = await _assigner.MoveUntrackedPathToAuthorFolderAsync(file.FullPath, root, null, unknown, ct);
                file.AuthorId = unknown.Id;
                file.BookId = null;
                file.ManuallyUnmatched = false;
                file.AuthorFolder = unknown.CalibreFolderName ?? unknown.Name;
                file.TitleFolder = Directory.Exists(finalPath) ? Path.GetFileName(finalPath) : Path.GetFileNameWithoutExtension(finalPath);
                file.FullPath = finalPath;
                file.NormalizedTitle = TitleNormalizer.Normalize(file.TitleFolder);
                file.ResetIntegrity();
                mapped++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{Path.GetFileName(file.FullPath)}: {ex.Message}");
            }
        }
        await _db.SaveChangesAsync(ct);
        return Ok(new AssignUnknownResult(mapped, warnings));
    }

    [HttpDelete("~/api/unclaimed")]
    public async Task<IActionResult> DiscardUnclaimed([FromQuery] string folder, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return BadRequest(new { error = "folder is required" });

        var incomingSetting = await _db.AppSettings
            .FirstOrDefaultAsync(s => s.Key == AppSettingKeys.IncomingFolder, ct);
        var incomingPath = incomingSetting?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(incomingPath))
            return BadRequest(new { error = "Incoming folder is not configured — set it in Settings first." });
        if (!Directory.Exists(incomingPath))
            return BadRequest(new { error = $"Incoming folder does not exist: {incomingPath}" });

        var files = await _db.LocalBookFiles
            .Where(f => f.AuthorFolder == folder && f.AuthorId == null)
            .ToListAsync(ct);

        if (files.Count == 0)
            return NotFound(new { error = $"No untracked files found for folder '{folder}'" });

        var authorDestRoot = UniqueDirectory(incomingPath, folder);
        try { Directory.CreateDirectory(authorDestRoot); }
        catch (IOException ex)
        {
            return StatusCode(500, new { error = $"Could not create destination folder: {ex.Message}" });
        }

        var movedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var moveWarnings = new List<string>();
        string? authorDirOnDisk = null;

        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.FullPath)) continue;
            var src = file.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(src)) continue;
            if (!movedSources.Add(src)) continue;

            authorDirOnDisk ??= Path.GetDirectoryName(src);

            var leaf = !string.IsNullOrWhiteSpace(file.TitleFolder) ? file.TitleFolder : Path.GetFileName(src);
            if (string.IsNullOrWhiteSpace(leaf)) leaf = $"returned-{file.Id}";

            var dest = UniqueDirectory(authorDestRoot, leaf);
            try { SafeMove.Directory(src, dest); }
            catch (IOException ex) { moveWarnings.Add($"{leaf}: {ex.Message}"); }
        }

        // Prune the now-empty author folder from the library.
        if (!string.IsNullOrWhiteSpace(authorDirOnDisk)
            && Directory.Exists(authorDirOnDisk)
            && !Directory.EnumerateFileSystemEntries(authorDirOnDisk).Any())
        {
            try { Directory.Delete(authorDirOnDisk); } catch { /* best effort */ }
        }

        // If nothing actually moved, clean up the empty dest root we created.
        if (Directory.Exists(authorDestRoot)
            && !Directory.EnumerateFileSystemEntries(authorDestRoot).Any())
        {
            try { Directory.Delete(authorDestRoot); } catch { /* best effort */ }
        }

        _db.LocalBookFiles.RemoveRange(files);

        var normalizedFolder = TitleNormalizer.NormalizeAuthor(folder);
        if (!string.IsNullOrEmpty(normalizedFolder)
            && !await _db.AuthorBlacklist.AnyAsync(b => b.NormalizedName == normalizedFolder, ct))
        {
            _db.AuthorBlacklist.Add(new AuthorBlacklist
            {
                Name = folder,
                NormalizedName = normalizedFolder,
                FolderName = folder,
                AddedAt = DateTime.UtcNow,
                Reason = "Discarded untracked folder"
            });
        }

        await _db.SaveChangesAsync(ct);

        if (moveWarnings.Count > 0)
            return Ok(new { warnings = moveWarnings });

        return NoContent();
    }

    // Moves every untracked file back to incoming in one shot.
    // Unlike the per-folder DELETE, this does NOT blacklist — the intent is
    // a bulk reset so the incoming processor can re-evaluate everything.
    [HttpDelete("~/api/unclaimed/all")]
    public async Task<IActionResult> DiscardAllUnclaimed(CancellationToken ct)
    {
        var incomingSetting = await _db.AppSettings
            .FirstOrDefaultAsync(s => s.Key == AppSettingKeys.IncomingFolder, ct);
        var incomingPath = incomingSetting?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(incomingPath))
            return BadRequest(new { error = "Incoming folder is not configured — set it in Settings first." });
        if (!Directory.Exists(incomingPath))
            return BadRequest(new { error = $"Incoming folder does not exist: {incomingPath}" });

        var allFiles = await _db.LocalBookFiles
            .Where(f => f.AuthorId == null)
            .ToListAsync(ct);

        if (allFiles.Count == 0)
            return NoContent();

        var moveWarnings = new List<string>();

        foreach (var group in allFiles.GroupBy(f => f.AuthorFolder))
        {
            var folder = group.Key;
            var authorDestRoot = UniqueDirectory(incomingPath, folder);
            try { Directory.CreateDirectory(authorDestRoot); }
            catch (IOException ex)
            {
                moveWarnings.Add($"{folder}: Could not create destination — {ex.Message}");
                continue;
            }

            var movedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? authorDirOnDisk = null;

            foreach (var file in group)
            {
                if (string.IsNullOrWhiteSpace(file.FullPath)) continue;
                var src = file.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!Directory.Exists(src)) continue;
                if (!movedSources.Add(src)) continue;

                authorDirOnDisk ??= Path.GetDirectoryName(src);

                var leaf = !string.IsNullOrWhiteSpace(file.TitleFolder) ? file.TitleFolder : Path.GetFileName(src);
                if (string.IsNullOrWhiteSpace(leaf)) leaf = $"returned-{file.Id}";

                var dest = UniqueDirectory(authorDestRoot, leaf);
                try { SafeMove.Directory(src, dest); }
                catch (IOException ex) { moveWarnings.Add($"{leaf}: {ex.Message}"); }
            }

            if (!string.IsNullOrWhiteSpace(authorDirOnDisk)
                && Directory.Exists(authorDirOnDisk)
                && !Directory.EnumerateFileSystemEntries(authorDirOnDisk).Any())
            {
                try { Directory.Delete(authorDirOnDisk); } catch { /* best effort */ }
            }

            if (Directory.Exists(authorDestRoot)
                && !Directory.EnumerateFileSystemEntries(authorDestRoot).Any())
            {
                try { Directory.Delete(authorDestRoot); } catch { /* best effort */ }
            }
        }

        _db.LocalBookFiles.RemoveRange(allFiles);
        await _db.SaveChangesAsync(ct);

        if (moveWarnings.Count > 0)
            return Ok(new { warnings = moveWarnings });

        return NoContent();
    }

    public sealed record UnknownFolder(string AuthorFolder, int FileCount, IReadOnlyList<string> RootPaths, IReadOnlyList<string> Formats, bool IsFile,
        // Integrity-check tally for files in this folder (UnknownFileChecks table).
        // A check is valid when SizeBytes+ModifiedAt still match the UnknownFile row.
        // There is no "damaged" state for unknown files — only ok or unchecked.
        int IntegrityOk = 0, int IntegrityUnchecked = 0,
        // Disk modified time (folder mtime, or the file's own for a loose file) so
        // the UI can default to newest-first — a file just moved here by the
        // incoming job sorts straight to the top instead of being buried
        // alphabetically among the quarantine backlog.
        DateTime ModifiedAt = default);

    // Lists author-level folders AND loose book files that exist inside the
    // __unknown quarantine bucket across all enabled library locations. Loose
    // files (books sitting directly at the quarantine root, not in any folder)
    // get IsFile=true so the client renders file-appropriate actions.
    [HttpGet("~/api/unknown-folders")]
    public async Task<IReadOnlyList<UnknownFolder>> ListUnknownFolders(CancellationToken ct)
    {
        var locations = await _db.LibraryLocations
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);

        var customUnknown = await UnknownFolderResolver.GetCustomPathAsync(_db, ct);
        // With a custom path, every folder reports it as the RootPath sentinel
        // — that's what the client passes back on delete/match actions.
        var scanRoots = customUnknown is not null
            ? new[] { (UnknownRoot: customUnknown, RootPath: customUnknown) }
            : locations.Select(l => (UnknownRoot: Path.Combine(l, CalibreScanner.UnknownAuthorFolder), RootPath: l)).ToArray();

        var result = new List<(string Folder, int Count, string RootPath, DateTime Modified)>();
        var looseFiles = new List<(string Name, string RootPath, string Ext, DateTime Modified)>();
        foreach (var (unknownRoot, rootPath) in scanRoots)
        {
            if (!Directory.Exists(unknownRoot)) continue;
            foreach (var dir in Directory.GetDirectories(unknownRoot))
            {
                var fileCount = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;
                if (fileCount > 0)
                {
                    // Folder mtime updates when the incoming job moves files into
                    // it, so it's a good "newest activity" proxy without statting
                    // every file inside (cheap on the NAS mount).
                    DateTime modified;
                    try { modified = Directory.GetLastWriteTimeUtc(dir); } catch { modified = default; }
                    result.Add((Folder: Path.GetFileName(dir), Count: fileCount, RootPath: rootPath, Modified: modified));
                }
            }
            foreach (var file in Directory.GetFiles(unknownRoot))
            {
                var ext = Path.GetExtension(file);
                if (CalibreScanner.EbookExtensions.Contains(ext) || CalibreScanner.ArchiveExtensions.Contains(ext))
                {
                    DateTime modified;
                    try { modified = System.IO.File.GetLastWriteTimeUtc(file); } catch { modified = default; }
                    looseFiles.Add((Name: Path.GetFileName(file), RootPath: rootPath, Ext: ext.TrimStart('.').ToLowerInvariant(), Modified: modified));
                }
            }
        }

        // Load UnknownFiles and their valid integrity checks so we can show
        // per-folder integrity status. A check is valid when the file's
        // SizeBytes and ModifiedAt still match the check record.
        var unknownFiles = await _db.UnknownFiles.AsNoTracking()
            .Select(f => new { f.FullPath, f.SizeBytes, f.ModifiedAt })
            .ToListAsync(ct);

        var checks = await _db.UnknownFileChecks.AsNoTracking()
            .Select(c => new { c.FullPath, c.SizeBytes, c.ModifiedAt })
            .ToListAsync(ct);

        // Build a set of fully-checked paths (valid = size+modified still match).
        var checkedPaths = new HashSet<string>(
            checks
                .Join(unknownFiles,
                    c => c.FullPath,
                    f => f.FullPath,
                    (c, f) => new { c.FullPath, Valid = c.SizeBytes == f.SizeBytes && c.ModifiedAt == f.ModifiedAt })
                .Where(x => x.Valid)
                .Select(x => x.FullPath),
            StringComparer.OrdinalIgnoreCase);

        // Map each indexed file to its parent folder name (one level below the unknown root).
        // We need this to aggregate per-folder counts.
        var filesByFolder = unknownFiles
            .Select(f =>
            {
                foreach (var (unknownRoot, _) in scanRoots)
                {
                    if (f.FullPath.StartsWith(unknownRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        var rel = f.FullPath.Substring(unknownRoot.Length).TrimStart(Path.DirectorySeparatorChar, '/');
                        var slash = rel.IndexOfAny(new[] { Path.DirectorySeparatorChar, '/' });
                        if (slash > 0)
                            return (FolderName: rel.Substring(0, slash), FilePath: f.FullPath, IsLoose: false);
                        // Loose file at the unknown root — folder key is the filename itself.
                        return (FolderName: rel, FilePath: f.FullPath, IsLoose: true);
                    }
                }
                return (FolderName: (string?)null, FilePath: f.FullPath, IsLoose: false);
            })
            .Where(x => x.FolderName != null)
            .GroupBy(x => (x.FolderName!, x.IsLoose))
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.FilePath).ToList());

        static (int Ok, int Unchecked) CountIntegrity(IEnumerable<string> paths, HashSet<string> checkedSet)
        {
            int ok = 0, unchecked_ = 0;
            foreach (var p in paths)
            {
                if (checkedSet.Contains(p)) ok++;
                else unchecked_++;
            }
            return (ok, unchecked_);
        }

        var folders = result
            .GroupBy(r => r.Folder)
            .Select(g =>
            {
                var paths = filesByFolder.TryGetValue((g.Key, false), out var fp) ? fp : [];
                var (ok, unc) = CountIntegrity(paths, checkedPaths);
                return new UnknownFolder(
                    g.Key,
                    g.Sum(x => x.Count),
                    g.Select(x => x.RootPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    g.SelectMany(x => FormatsInUnknownFolder(x.RootPath, x.Folder))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x)
                        .ToList(),
                    IsFile: false,
                    IntegrityOk: ok,
                    IntegrityUnchecked: unc,
                    ModifiedAt: g.Max(x => x.Modified));
            });

        var files = looseFiles
            .GroupBy(f => f.Name)
            .Select(g =>
            {
                var paths = filesByFolder.TryGetValue((g.Key, true), out var fp) ? fp : [];
                var (ok, unc) = CountIntegrity(paths, checkedPaths);
                return new UnknownFolder(
                    g.Key,
                    g.Count(),
                    g.Select(x => x.RootPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    g.Select(x => x.Ext).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList(),
                    IsFile: true,
                    IntegrityOk: ok,
                    IntegrityUnchecked: unc,
                    ModifiedAt: g.Max(x => x.Modified));
            });

        return folders.Concat(files)
            .OrderByDescending(u => u.ModifiedAt)
            .ThenBy(u => u.AuthorFolder)
            .ToList();
    }

    // Moves a single __unknown author folder back to the incoming bucket so it
    // can be re-evaluated after the user adds the author to the watchlist.
    [HttpDelete("~/api/unknown-folders")]
    public async Task<IActionResult> ReturnUnknownFolder([FromQuery] string folder, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return BadRequest(new { error = "folder is required" });

        var incomingSetting = await _db.AppSettings
            .FirstOrDefaultAsync(s => s.Key == AppSettingKeys.IncomingFolder, ct);
        var incomingPath = incomingSetting?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(incomingPath))
            return BadRequest(new { error = "Incoming folder is not configured — set it in Settings first." });
        if (!Directory.Exists(incomingPath))
            return BadRequest(new { error = $"Incoming folder does not exist: {incomingPath}" });

        var locations = await _db.LibraryLocations
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);

        var unknownRoots = await UnknownFolderResolver.GetSourceRootsAsync(_db, locations, ct);

        var warnings = new List<string>();
        bool found = false;
        foreach (var unknownRoot in unknownRoots)
        {
            var src = Path.Combine(unknownRoot, folder);

            // Loose book file sitting directly at the quarantine root — move
            // just that file back to incoming.
            if (System.IO.File.Exists(src))
            {
                found = true;
                try
                {
                    SafeMove.File(src, UniqueFilePath(Path.Combine(incomingPath, folder)));
                }
                catch (IOException ex) { warnings.Add(ex.Message); }
                continue;
            }

            if (!Directory.Exists(src)) continue;
            found = true;
            var dest = UniqueDirectory(incomingPath, folder);
            try
            {
                Directory.CreateDirectory(dest);
                foreach (var entry in Directory.GetFileSystemEntries(src))
                {
                    var name = Path.GetFileName(entry);
                    var target = Path.Combine(dest, name);
                    if (Directory.Exists(entry)) SafeMove.Directory(entry, target);
                    else SafeMove.File(entry, target, overwrite: false);
                }
                if (!Directory.EnumerateFileSystemEntries(src).Any())
                    Directory.Delete(src);
            }
            catch (IOException ex) { warnings.Add(ex.Message); }
        }

        if (!found)
            return NotFound(new { error = $"Folder '{folder}' not found in __unknown" });

        if (warnings.Count > 0)
            return Ok(new { warnings });

        return NoContent();
    }

    // Moves ALL __unknown author folders back to incoming in one shot.
    [HttpDelete("~/api/unknown-folders/all")]
    public async Task<IActionResult> ReturnAllUnknownFolders(CancellationToken ct)
    {
        var incomingSetting = await _db.AppSettings
            .FirstOrDefaultAsync(s => s.Key == AppSettingKeys.IncomingFolder, ct);
        var incomingPath = incomingSetting?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(incomingPath))
            return BadRequest(new { error = "Incoming folder is not configured — set it in Settings first." });
        if (!Directory.Exists(incomingPath))
            return BadRequest(new { error = $"Incoming folder does not exist: {incomingPath}" });

        var locations = await _db.LibraryLocations
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);

        var unknownRoots = await UnknownFolderResolver.GetSourceRootsAsync(_db, locations, ct);

        var warnings = new List<string>();
        foreach (var unknownRoot in unknownRoots)
        {
            if (!Directory.Exists(unknownRoot)) continue;
            foreach (var dir in Directory.GetDirectories(unknownRoot))
            {
                var folderName = Path.GetFileName(dir);
                var dest = UniqueDirectory(incomingPath, folderName);
                try
                {
                    Directory.CreateDirectory(dest);
                    foreach (var entry in Directory.GetFileSystemEntries(dir))
                    {
                        var name = Path.GetFileName(entry);
                        var target = Path.Combine(dest, name);
                        if (Directory.Exists(entry)) SafeMove.Directory(entry, target);
                        else SafeMove.File(entry, target, overwrite: false);
                    }
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch (IOException ex) { warnings.Add($"{folderName}: {ex.Message}"); }
            }

            // Loose book files at the quarantine root go back to incoming too.
            foreach (var file in Directory.GetFiles(unknownRoot))
            {
                var ext = Path.GetExtension(file);
                if (!CalibreScanner.EbookExtensions.Contains(ext) && !CalibreScanner.ArchiveExtensions.Contains(ext))
                    continue;
                var name = Path.GetFileName(file);
                try
                {
                    SafeMove.File(file, UniqueFilePath(Path.Combine(incomingPath, name)));
                }
                catch (IOException ex) { warnings.Add($"{name}: {ex.Message}"); }
            }
        }

        if (warnings.Count > 0)
            return Ok(new { warnings });

        return NoContent();
    }

    // Kicks off the author-folder disambiguator (also runs daily via Hangfire
    // at 11:00). Returns the previous run's summary if one is already in
    // flight; otherwise schedules a new run and returns 202 Accepted.
    [HttpPost("disambiguate-folders")]
    public ActionResult<object> DisambiguateFolders(
        [FromServices] AuthorFolderDisambiguatorService service,
        CancellationToken ct)
    {
        if (service.IsRunning)
            return Accepted(new { running = true, lastResult = service.LastResult });
        if (!service.TryStart(ct, out var error))
            return Conflict(new { error });
        return Accepted(new { running = true });
    }

    [HttpGet("disambiguate-folders/status")]
    public ActionResult<object> DisambiguateFoldersStatus(
        [FromServices] AuthorFolderDisambiguatorService service)
    {
        return Ok(new { running = service.IsRunning, lastResult = service.LastResult });
    }

    public sealed record OlSuggestion(string OpenLibraryKey, string Name, int? WorkCount, double Score);

    // Given a folder name, returns top OpenLibrary author candidates ranked by
    // a fuzzy score against the folder name. Used on the Untracked page so a
    // user can promote a quarantined folder to a tracked author in one click
    // without typing into the search dialog. Rate-limited via the shared OL
    // limiter so a barrage of folder names won't violate OL's 1/sec ceiling.
    [HttpGet("~/api/openlibrary/suggest-for-folder")]
    public async Task<ActionResult<IReadOnlyList<OlSuggestion>>> SuggestForFolder(
        [FromQuery] string folder, CancellationToken ct,
        [FromQuery] int top = 3)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return BadRequest(new { error = "folder is required" });

        // The "Last, First" sort form gets reordered for the OL query so the
        // search picks up the canonical "First Last" form OL prefers.
        var query = folder;
        if (query.Contains(','))
        {
            var parts = query.Split(',', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2) query = $"{parts[1]} {parts[0]}";
        }

        var resp = await _ol.SearchAuthorsAsync(query, ct);
        var folderKey = TitleNormalizer.NormalizeAuthor(folder);
        var suggestions = resp?.Docs
            ?.Where(d => !string.IsNullOrEmpty(d.Key) && !string.IsNullOrEmpty(d.Name))
            .Select(d => new OlSuggestion(
                d.Key!, d.Name!, d.WorkCount,
                FuzzyScore.JaroWinkler(folderKey, TitleNormalizer.NormalizeAuthor(d.Name!))))
            .OrderByDescending(s => s.Score)
            .Take(Math.Clamp(top, 1, 10))
            .ToList()
            ?? new List<OlSuggestion>();

        return Ok(suggestions);
    }

    public sealed record UnknownMatchResult(
        string FolderName,
        int AuthorId,
        string AuthorName);

    public sealed record UnknownMatchSummary(
        int Matched,
        int Unmatched,
        IReadOnlyList<UnknownMatchResult> Details,
        IReadOnlyList<string> Warnings);

    // Tries to re-match every __unknown folder against the current watchlist
    // (including OL alternate names). Matched folders are moved out of __unknown
    // and into the canonical author's folder; unmatched folders stay put. Run
    // this after adding authors so previously-quarantined collections fold back
    // in without a full sync.
    [HttpPost("~/api/unknown-folders/match")]
    public async Task<ActionResult<UnknownMatchSummary>> MatchUnknownFolders(CancellationToken ct)
    {
        // Build the matcher with tracked authors plus OL alternates joined by
        // OpenLibraryKey. Non-pen-name linked children are skipped so a folder
        // resolves directly to its canonical entry.
        var authors = await _db.Authors
            .Where(a => a.LinkedToAuthorId == null || a.IsPenName)
            .Select(a => new { a.Id, a.Name, a.CalibreFolderName, a.OpenLibraryKey })
            .ToListAsync(ct);

        var olKeys = authors
            .Where(a => !string.IsNullOrEmpty(a.OpenLibraryKey))
            .Select(a => a.OpenLibraryKey!)
            .Distinct()
            .ToList();
        var olAlternates = await _db.OpenLibraryAuthors
            .Where(o => olKeys.Contains(o.OlKey))
            .Select(o => new { o.OlKey, o.AlternateNames, o.PersonalName })
            .ToDictionaryAsync(o => o.OlKey, ct);

        var blacklisted = (await _db.AuthorBlacklist
            .Select(b => b.NormalizedName)
            .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var entries = authors.Select(a =>
        {
            var aliases = new List<string>();
            if (!string.IsNullOrEmpty(a.OpenLibraryKey)
                && olAlternates.TryGetValue(a.OpenLibraryKey, out var ol))
            {
                if (!string.IsNullOrWhiteSpace(ol.PersonalName)) aliases.Add(ol.PersonalName);
                if (!string.IsNullOrWhiteSpace(ol.AlternateNames))
                    aliases.AddRange(ol.AlternateNames.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            return new AuthorIndexEntry(
                DisplayName: a.Name,
                FolderName: string.IsNullOrWhiteSpace(a.CalibreFolderName) ? a.Name : a.CalibreFolderName!,
                IsTracked: true,
                TrackedAuthorId: a.Id,
                OpenLibraryKey: a.OpenLibraryKey,
                AlternateNames: aliases.Count == 0 ? null : aliases);
        });
        var matcher = new AuthorMatcher(entries, blacklisted);

        var locations = await _db.LibraryLocations
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);

        var customUnknown = await UnknownFolderResolver.GetCustomPathAsync(_db, ct);
        var primaryLocation = await _db.LibraryLocations
            .Where(l => l.Enabled && l.IsPrimary)
            .Select(l => l.Path)
            .FirstOrDefaultAsync(ct)
            ?? locations.FirstOrDefault();
        // Per-location pairs of (where to scan, where to move matches into).
        var scanPlan = customUnknown is not null
            ? new[] { (UnknownRoot: customUnknown, DestRoot: primaryLocation ?? customUnknown) }
            : locations.Select(l => (UnknownRoot: Path.Combine(l, CalibreScanner.UnknownAuthorFolder), DestRoot: l)).ToArray();

        var warnings = new List<string>();
        var details = new List<UnknownMatchResult>();
        int unmatched = 0;

        foreach (var (unknownRoot, root) in scanPlan)
        {
            if (!Directory.Exists(unknownRoot)) continue;

            var folderNames = Directory.EnumerateDirectories(unknownRoot)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Cast<string>()
                .ToList();

            var plan = UnknownFolderRecovery.Plan(folderNames, matcher);
            unmatched += plan.Unmatched.Count;

            foreach (var decision in plan.Matched)
            {
                var src = Path.Combine(unknownRoot, decision.FolderName);
                if (!Directory.Exists(src)) continue;
                var entry = decision.Match!;
                var destLeaf = string.IsNullOrWhiteSpace(entry.FolderName) ? entry.DisplayName : entry.FolderName;
                var dest = Path.Combine(root, SanitizeSegment(destLeaf));

                try
                {
                    if (Directory.Exists(dest))
                    {
                        // Canonical folder already exists — merge by moving each
                        // child entry across rather than failing the rename.
                        foreach (var child in Directory.GetFileSystemEntries(src))
                        {
                            var childName = Path.GetFileName(child);
                            var target = UniqueDirectory(dest, childName);
                            if (Directory.Exists(child)) SafeMove.Directory(child, target);
                            else SafeMove.File(child, target, overwrite: false);
                        }
                        if (!Directory.EnumerateFileSystemEntries(src).Any())
                            Directory.Delete(src);
                    }
                    else
                    {
                        SafeMove.Directory(src, dest);
                    }

                    if (entry.TrackedAuthorId is int authorId)
                        details.Add(new UnknownMatchResult(decision.FolderName, authorId, entry.DisplayName));
                }
                catch (IOException ex)
                {
                    warnings.Add($"{decision.FolderName}: {ex.Message}");
                }
            }
        }

        return Ok(new UnknownMatchSummary(details.Count, unmatched, details, warnings));
    }
}

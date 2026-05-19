using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

// Enforces the canonical flat-file layout for all matched ebook files:
//   {libRoot}\{authorFolder}\{seriesName}\book.epub   (book has a Series)
//   {libRoot}\{authorFolder}\book.epub                (book has no Series)
//
// Title subfolders are eliminated — ebook files live directly in the series
// or author folder. All files inside a title folder are moved together so the
// folder becomes empty and is then deleted. FullPath and TitleFolder in the DB
// are updated immediately after each move so a subsequent sync sees the correct
// paths and does not recreate or lose records.
//
// Books already in the correct location are skipped. Name conflicts at the
// destination are resolved by appending _{n} to the file stem.
//
// Runs as a singleton through BackgroundTaskCoordinator — cannot overlap with
// sync, incoming, or any other background job.
public sealed class SeriesOrganizerService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly ILogger<SeriesOrganizerService> _log;
    private volatile bool _isRunning;

    public SeriesOrganizerService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        ILogger<SeriesOrganizerService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _log = log;
    }

    public bool IsRunning => _isRunning;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("series-organizer", out var holder))
        {
            error = $"Another task is already running ({holder})";
            return false;
        }
        error = null;
        _isRunning = true;
        _ = Task.Run(async () =>
        {
            try { await RunAsync(hostCt); }
            catch (OperationCanceledException) when (hostCt.IsCancellationRequested) { }
            catch (Exception ex) { _log.LogError(ex, "Series organizer failed"); }
            finally { _isRunning = false; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        _log.LogInformation("Series organizer starting");
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

        var locations = await db.LibraryLocations
            .Where(l => l.Enabled)
            .ToListAsync(ct);

        _log.LogInformation("Series organizer: {LocCount} enabled location(s): {Paths}",
            locations.Count, string.Join("; ", locations.Select(l => l.Path)));

        if (locations.Count == 0)
        {
            _log.LogInformation("Series organizer: no enabled library locations");
            return;
        }

        // Load priorities via projection so we don't SELECT RefreshIntervalDays
        // (which may not yet exist in the DB if the migration is pending).
        var priorities = await db.Authors
            .Select(a => new { a.Id, a.Priority })
            .ToDictionaryAsync(a => a.Id, a => a.Priority, ct);

        var files = await db.LocalBookFiles
            .Include(f => f.Book).ThenInclude(b => b!.Series)
            .ToListAsync(ct);

        files.Sort((x, y) =>
        {
            var px = x.AuthorId.HasValue && priorities.TryGetValue(x.AuthorId.Value, out var xp) ? xp : 0;
            var py = y.AuthorId.HasValue && priorities.TryGetValue(y.AuthorId.Value, out var yp) ? yp : 0;
            if (py != px) return py.CompareTo(px);
            var af = string.Compare(x.AuthorFolder, y.AuthorFolder, StringComparison.OrdinalIgnoreCase);
            if (af != 0) return af;
            return string.Compare(x.FullPath, y.FullPath, StringComparison.OrdinalIgnoreCase);
        });

        _log.LogInformation("Series organizer: {Count} LocalBookFile record(s) to evaluate", files.Count);

        // Track current FullPath for every record so we can detect unique-index
        // conflicts before hitting the DB. Updated as records change.
        var pathIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
            pathIndex.TryAdd(f.FullPath, f.Id);

        int moved = 0, skipped = 0, errors = 0;
        int cntNoLocation = 0, cntNotFound = 0, cntAlreadyCorrect = 0, cntNullContainer = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            // Match against a known location using the stored path. If the path is
            // a UNC path (\\server\share\rest) but the location is a container mount
            // (/rest), strip the \\server\share prefix and retry so files recorded
            // under a Windows UNC path are still processed.
            var location = locations.FirstOrDefault(l =>
                file.FullPath.StartsWith(l.Path, StringComparison.OrdinalIgnoreCase));

            string effectivePath = file.FullPath;
            if (location is null)
            {
                var stripped = UncToAbsolutePath(file.FullPath);
                if (!string.Equals(stripped, file.FullPath, StringComparison.OrdinalIgnoreCase))
                {
                    location = locations.FirstOrDefault(l =>
                        stripped.StartsWith(l.Path, StringComparison.OrdinalIgnoreCase));
                    if (location is not null)
                        effectivePath = stripped;
                }
            }

            if (location is null)
            {
                if (cntNoLocation == 0)
                    _log.LogWarning("Skip (no location match) e.g.: {Path}", file.FullPath);
                cntNoLocation++;
                skipped++;
                continue;
            }

            var libRoot = location.Path.TrimEnd('\\', '/');
            var authorDir = Path.Combine(libRoot, file.AuthorFolder);

            // Series resolution — three steps, in priority order:
            //
            // 1. Book.Series from DB: the user's explicit value always wins.
            //    A null value means "not yet known" (fallthrough); an empty
            //    string means "user explicitly cleared it" → author root.
            //
            // 2. Auto-clean bad stored values: if Book.Series itself looks like
            //    a title-folder string ("Midkemia 02 - The King's Buccaneer"),
            //    extract the clean series name and fix the DB so the correct
            //    folder is used from now on.
            //
            // 3. Filename fallback: when DB series is null, parse the filename
            //    ("Midkemia 02 - Title.epub" → "Midkemia") and backfill the DB.
            var stem = Path.GetFileNameWithoutExtension(effectivePath);
            // series is now the Name string from the Series navigation property (or null/empty)
            var seriesName = file.Book?.Series?.Name;

            if (!string.IsNullOrWhiteSpace(seriesName))
            {
                // Step 2 — clean up values that look like title-folder format.
                var (cleanedSeries, _, _, _) = TitleNormalizer.TryParseSeriesFilename(seriesName);
                if (!string.IsNullOrWhiteSpace(cleanedSeries)
                    && !string.Equals(cleanedSeries, seriesName, StringComparison.OrdinalIgnoreCase))
                {
                    seriesName = cleanedSeries;
                    if (file.BookId.HasValue)
                    {
                        var normalizedCleaned = TitleNormalizer.Normalize(cleanedSeries);
                        var cleanedRecord = await db.Series.FirstOrDefaultAsync(
                            s => s.NormalizedName == normalizedCleaned, ct);
                        if (cleanedRecord is null)
                        {
                            cleanedRecord = new Series
                            {
                                Name = cleanedSeries,
                                NormalizedName = normalizedCleaned,
                                PrimaryAuthorId = file.AuthorId,
                            };
                            db.Series.Add(cleanedRecord);
                            await db.SaveChangesAsync(ct);
                        }
                        var cleanedSeriesId = cleanedRecord.Id;
                        await db.Books
                            .Where(b => b.Id == file.BookId.Value)
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(b => b.SeriesId, _ => cleanedSeriesId), ct);
                    }
                }
            }
            else if (seriesName is null && file.Book?.SeriesId is null)
            {
                // Step 3 — filename fallback only when the DB has no series at all.
                var (parsedSeries, parsedPos, _, _) = TitleNormalizer.TryParseSeriesFilename(stem);
                if (!string.IsNullOrWhiteSpace(parsedSeries))
                {
                    seriesName = parsedSeries;
                    if (file.BookId.HasValue)
                    {
                        var normalizedParsed = TitleNormalizer.Normalize(parsedSeries);
                        var parsedRecord = await db.Series.FirstOrDefaultAsync(
                            s => s.NormalizedName == normalizedParsed, ct);
                        if (parsedRecord is null)
                        {
                            parsedRecord = new Series
                            {
                                Name = parsedSeries,
                                NormalizedName = normalizedParsed,
                                PrimaryAuthorId = file.AuthorId,
                            };
                            db.Series.Add(parsedRecord);
                            await db.SaveChangesAsync(ct);
                        }
                        var parsedSeriesId = parsedRecord.Id;
                        await db.Books
                            .Where(b => b.Id == file.BookId.Value && b.SeriesId == null)
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(b => b.SeriesId, _ => parsedSeriesId)
                                .SetProperty(b => b.SeriesPosition, b =>
                                    b.SeriesPosition == null ? parsedPos : b.SeriesPosition),
                            ct);
                    }
                }
            }
            // seriesName == null (book has SeriesId set) means it was already loaded;
            // seriesName == "" → not possible via nav property (Series.Name is never empty after migration)
            // A book with no Series navigation → targetDir = authorDir (no series folder)

            var targetDir = string.IsNullOrWhiteSpace(seriesName)
                ? authorDir
                : Path.Combine(authorDir, SanitizeFolderName(seriesName));

            string? sourceContainer;
            string? primaryEbook;

            if (File.Exists(effectivePath))
            {
                sourceContainer = Path.GetDirectoryName(effectivePath);
                primaryEbook = effectivePath;
            }
            else if (Directory.Exists(effectivePath))
            {
                sourceContainer = effectivePath;
                primaryEbook = PrimaryEbook(effectivePath);
            }
            else
            {
                if (cntNotFound == 0)
                    _log.LogWarning("Skip (not found on disk) e.g.: {Path}", effectivePath);
                cntNotFound++;
                skipped++;
                continue;
            }

            if (sourceContainer is null)
            {
                cntNullContainer++;
                skipped++;
                continue;
            }

            // Already in the right directory — update FullPath from folder→file or
            // from stale UNC→container path, unless subdirs still need flattening.
            if (string.Equals(sourceContainer, targetDir, StringComparison.OrdinalIgnoreCase))
            {
                bool hasSubdirFiles = false;
                // Only look for title-subfolder remnants when the DB record still
                // points to a directory (old Calibre layout). If the record already
                // points to a specific file, there is nothing for THIS record to
                // flatten — scanning targetDir would falsely trigger on unrelated
                // sibling series subfolders (e.g. a no-series file in authorDir
                // seeing the series folders of other books).
                bool recordIsDirectory = !File.Exists(effectivePath) && Directory.Exists(effectivePath);
                if (recordIsDirectory)
                {
                    try
                    {
                        hasSubdirFiles = Directory.EnumerateDirectories(targetDir).Any() &&
                            Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories)
                                .Any(f => !string.Equals(
                                    Path.GetDirectoryName(f), targetDir,
                                    StringComparison.OrdinalIgnoreCase));
                    }
                    catch { }
                }

                if (!hasSubdirFiles)
                {
                    var wantPath = primaryEbook ?? effectivePath;
                    if (!string.Equals(file.FullPath, wantPath, StringComparison.OrdinalIgnoreCase))
                    {
                        if (pathIndex.TryGetValue(wantPath, out var conflictId) && conflictId != file.Id)
                        {
                            _log.LogDebug("Remove stale pointer [{Lbf}]: {Path} (superseded by [{Other}])",
                                file.Id, file.FullPath, conflictId);
                            pathIndex.Remove(file.FullPath);
                            await db.LocalBookFiles
                                .Where(f => f.Id == file.Id)
                                .ExecuteDeleteAsync(ct);
                        }
                        else
                        {
                            _log.LogDebug("FixPath [{Lbf}]: {Old} -> {New}", file.Id, file.FullPath, wantPath);
                            pathIndex.Remove(file.FullPath);
                            var fpTitle = primaryEbook is not null
                                ? Path.GetFileNameWithoutExtension(primaryEbook)
                                : file.TitleFolder;
                            await db.LocalBookFiles
                                .Where(f => f.Id == file.Id)
                                .ExecuteUpdateAsync(s => s
                                    .SetProperty(f => f.FullPath, wantPath)
                                    .SetProperty(f => f.TitleFolder, fpTitle), ct);
                            file.FullPath = wantPath;
                            file.TitleFolder = fpTitle;
                            pathIndex[wantPath] = file.Id;
                        }
                    }
                    cntAlreadyCorrect++;
                    skipped++;
                    continue;
                }

                _log.LogDebug("Flatten: [{Lbf}] subdirs inside {Target}", file.Id, targetDir);
            }
            else
            {
                _log.LogDebug("Move: [{Lbf}] {Src} -> {Target}", file.Id, sourceContainer, targetDir);
            }

            try
            {
                Directory.CreateDirectory(targetDir);

                // Flat-file layout: FullPath points to a specific file sitting
                // directly in the author or series folder. Each LocalBookFile
                // record owns exactly one file — move only that file. Sweeping
                // the whole sourceContainer would drag sibling books (which
                // have their own records) into the wrong series subfolder.
                //
                // Classic Calibre layout: FullPath is a title folder whose
                // entire contents belong to this one book. Enumerate everything.
                bool isFlatFile = primaryEbook is not null
                    && File.Exists(primaryEbook)
                    && string.Equals(primaryEbook, effectivePath, StringComparison.OrdinalIgnoreCase);

                List<string> filesToMove;
                try
                {
                    filesToMove = isFlatFile
                        ? new List<string> { primaryEbook! }
                        : Directory.EnumerateFiles(sourceContainer, "*", SearchOption.AllDirectories).ToList();
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Series organizer: cannot read source {Path}", sourceContainer);
                    errors++;
                    continue;
                }

                // For directory records (old Calibre title-folder layout), exclude
                // files that already have their own LocalBookFile records. Moving
                // those would pull series-organised books out of their series
                // subfolders. If every file is owned by another record this is a
                // ghost directory entry — remove it and move on.
                if (!isFlatFile)
                {
                    filesToMove = filesToMove
                        .Where(f => !pathIndex.TryGetValue(f, out var fId) || fId == file.Id)
                        .ToList();
                    if (filesToMove.Count == 0)
                    {
                        _log.LogDebug(
                            "Ghost directory record [{Lbf}]: {Path} — all files owned by other records, removing",
                            file.Id, file.FullPath);
                        await db.LocalBookFiles
                            .Where(f => f.Id == file.Id)
                            .ExecuteDeleteAsync(ct);
                        pathIndex.Remove(file.FullPath);
                        continue;
                    }
                }

                string? newPrimary = null;
                var movedEbooks = new List<string>();
                foreach (var src in filesToMove)
                {
                    var ext = Path.GetExtension(src);

                    // Delete junk files in place — only relevant for the
                    // title-folder (classic) path; flat-file records point to
                    // known ebook/archive files so this branch is never hit.
                    if (CalibreScanner.JunkExtensions.Contains(ext))
                    {
                        try { File.Delete(src); }
                        catch (Exception ex) { _log.LogWarning(ex, "Could not delete junk file: {Path}", src); }
                        continue;
                    }

                    var destPath = DestinationPath(targetDir, src);
                    if (!string.Equals(src, destPath, StringComparison.OrdinalIgnoreCase))
                        File.Move(src, destPath);

                    if (string.Equals(src, primaryEbook, StringComparison.OrdinalIgnoreCase))
                        newPrimary = destPath;

                    if (CalibreScanner.EbookExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                        movedEbooks.Add(destPath);
                }

                if (newPrimary is null && movedEbooks.Count > 0)
                {
                    newPrimary = movedEbooks.FirstOrDefault(f =>
                                     Path.GetExtension(f).Equals(".epub", StringComparison.OrdinalIgnoreCase))
                                 ?? movedEbooks.FirstOrDefault(f =>
                                     Path.GetExtension(f).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                                 ?? movedEbooks[0];
                }

                // Only prune the source container when it was a dedicated title
                // folder — never delete the author dir or a series subfolder
                // that other books still live in.
                if (!isFlatFile)
                {
                    PruneEmptyDirs(sourceContainer);
                    DeleteEmptyAncestors(sourceContainer, authorDir, targetDir);
                }

                _log.LogDebug("Moved {Count} file(s): {Old} -> {New}",
                    filesToMove.Count, sourceContainer, targetDir);

                var newPath = newPrimary ?? (primaryEbook is null ? targetDir : null);
                if (newPath is not null && !string.Equals(file.FullPath, newPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (pathIndex.TryGetValue(newPath, out var conflictId) && conflictId != file.Id)
                    {
                        _log.LogDebug("Remove stale pointer [{Lbf}]: {Path} (superseded by [{Other}])",
                            file.Id, file.FullPath, conflictId);
                        pathIndex.Remove(file.FullPath);
                        // File.Move should have already removed the source, but on CIFS/NFS
                        // mounts the source deletion can be deferred. Delete explicitly so
                        // the sync scanner cannot re-import this file as a new record.
                        if (isFlatFile && primaryEbook is not null)
                        {
                            try
                            {
                                if (File.Exists(primaryEbook))
                                    File.Delete(primaryEbook);
                            }
                            catch (Exception ex)
                            {
                                _log.LogWarning(ex, "Could not delete stale duplicate: {Path}", primaryEbook);
                            }
                        }
                        await db.LocalBookFiles
                            .Where(f => f.Id == file.Id)
                            .ExecuteDeleteAsync(ct);
                    }
                    else
                    {
                        pathIndex.Remove(file.FullPath);
                        var mvTitle = newPrimary is not null
                            ? Path.GetFileNameWithoutExtension(newPrimary)
                            : file.TitleFolder;
                        await db.LocalBookFiles
                            .Where(f => f.Id == file.Id)
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(f => f.FullPath, newPath)
                                .SetProperty(f => f.TitleFolder, mvTitle), ct);
                        file.FullPath = newPath;
                        file.TitleFolder = mvTitle;
                        pathIndex[newPath] = file.Id;
                    }
                }

                moved++;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Series organizer: failed to process {Path}", effectivePath);
                errors++;
            }
        }

        _log.LogInformation(
            "Series organizer done. Moved={Moved} AlreadyCorrect={AlreadyCorrect} " +
            "NotFound={NotFound} NoLocation={NoLocation} NullContainer={NullContainer} Errors={Errors}",
            moved, cntAlreadyCorrect, cntNotFound, cntNoLocation, cntNullContainer, errors);
    }

    // Strips the \\server\share prefix from a UNC path (\\server\share\rest or
    // //server/share/rest) and returns the absolute path /rest. This lets the
    // organizer match files that were recorded as Windows UNC paths against
    // library locations stored as container-local mount paths.
    private static string UncToAbsolutePath(string path)
    {
        var n = path.Replace('\\', '/');
        if (!n.StartsWith("//")) return n;
        int i1 = n.IndexOf('/', 2);
        if (i1 < 0) return n;
        int i2 = n.IndexOf('/', i1 + 1);
        if (i2 < 0) return n;
        return n[i2..];
    }

    // Recursively deletes empty subdirectories bottom-up. Does not throw — any
    // directory that cannot be removed is silently left in place.
    private static void PruneEmptyDirs(string dir)
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
                PruneEmptyDirs(sub);
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch { }
    }

    // Deletes dir if it is empty and is not authorDir or targetDir, then recurses
    // upward so that intermediate grouping folders (e.g. "Misc" containing title
    // subfolders) are also removed once all their children have been emptied.
    private static void DeleteEmptyAncestors(string dir, string authorDir, string targetDir)
    {
        if (string.Equals(dir, authorDir, StringComparison.OrdinalIgnoreCase)) return;
        if (string.Equals(dir, targetDir, StringComparison.OrdinalIgnoreCase)) return;

        var parent = Path.GetDirectoryName(dir);

        if (Directory.Exists(dir))
        {
            if (Directory.EnumerateFileSystemEntries(dir).Any()) return;
            try { Directory.Delete(dir); }
            catch { return; }
        }

        if (parent is not null)
            DeleteEmptyAncestors(parent, authorDir, targetDir);
    }

    // Returns a unique destination path inside targetDir for a file being moved.
    // If targetDir\filename already exists, appends _{n} to the stem.
    private static string DestinationPath(string targetDir, string srcFile)
    {
        var name = Path.GetFileName(srcFile);
        var dest = Path.Combine(targetDir, name);
        if (!File.Exists(dest) ||
            string.Equals(dest, srcFile, StringComparison.OrdinalIgnoreCase))
            return dest;

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext  = Path.GetExtension(name);
        for (int n = 1; ; n++)
        {
            dest = Path.Combine(targetDir, $"{stem}_{n}{ext}");
            if (!File.Exists(dest)) return dest;
        }
    }

    // Returns the primary ebook file inside a folder (epub preferred, then pdf, then any).
    private static string? PrimaryEbook(string folder)
    {
        try
        {
            var files = Directory.EnumerateFiles(folder)
                .Where(f => CalibreScanner.EbookExtensions.Contains(Path.GetExtension(f)))
                .ToList();
            return files.FirstOrDefault(f => Path.GetExtension(f).Equals(".epub", StringComparison.OrdinalIgnoreCase))
                ?? files.FirstOrDefault(f => Path.GetExtension(f).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                ?? files.FirstOrDefault();
        }
        catch { return null; }
    }

    internal static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        var result = sb.ToString().TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(result) ? "_" : result;
    }
}

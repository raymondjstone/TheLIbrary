using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
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
            .Include(f => f.Book)
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
        if (files.Count > 0)
            _log.LogInformation("Series organizer: sample FullPath = {Sample}", files[0].FullPath);

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

            var series = file.Book?.Series;

            // When the DB has no series (OL hasn't supplied one yet), try to
            // extract it from the filename — e.g. "Chaoswar Saga 03 - Title.epub".
            if (string.IsNullOrWhiteSpace(series))
            {
                var stem = Path.GetFileNameWithoutExtension(effectivePath);
                var (parsedSeries, parsedPos, _, _) = TitleNormalizer.TryParseSeriesFilename(stem);
                if (!string.IsNullOrWhiteSpace(parsedSeries))
                {
                    series = parsedSeries;
                    // Persist so subsequent runs (and the UI) don't need to re-parse.
                    if (file.BookId.HasValue)
                    {
                        await db.Books
                            .Where(b => b.Id == file.BookId.Value && b.Series == null)
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(b => b.Series, _ => parsedSeries)
                                .SetProperty(b => b.SeriesPosition, b =>
                                    b.SeriesPosition == null ? parsedPos : b.SeriesPosition),
                            ct);
                    }
                }
            }

            var targetDir = string.IsNullOrWhiteSpace(series)
                ? authorDir
                : Path.Combine(authorDir, SanitizeFolderName(series));

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
                try
                {
                    hasSubdirFiles = Directory.EnumerateDirectories(targetDir).Any() &&
                        Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories)
                            .Any(f => !string.Equals(
                                Path.GetDirectoryName(f), targetDir,
                                StringComparison.OrdinalIgnoreCase));
                }
                catch { }

                if (!hasSubdirFiles)
                {
                    var wantPath = primaryEbook ?? effectivePath;
                    if (!string.Equals(file.FullPath, wantPath, StringComparison.OrdinalIgnoreCase))
                    {
                        if (pathIndex.TryGetValue(wantPath, out var conflictId) && conflictId != file.Id)
                        {
                            _log.LogInformation("Remove stale pointer [{Lbf}]: {Path} (superseded by [{Other}])",
                                file.Id, file.FullPath, conflictId);
                            pathIndex.Remove(file.FullPath);
                            db.LocalBookFiles.Remove(file);
                        }
                        else
                        {
                            _log.LogInformation("FixPath [{Lbf}]: {Old} -> {New}", file.Id, file.FullPath, wantPath);
                            pathIndex.Remove(file.FullPath);
                            file.FullPath = wantPath;
                            if (primaryEbook is not null)
                                file.TitleFolder = Path.GetFileNameWithoutExtension(primaryEbook)!;
                            pathIndex[wantPath] = file.Id;
                        }
                        await db.SaveChangesAsync(ct);
                    }
                    cntAlreadyCorrect++;
                    skipped++;
                    continue;
                }

                _log.LogInformation("Flatten: [{Lbf}] subdirs inside {Target}", file.Id, targetDir);
            }
            else
            {
                _log.LogInformation("Move: [{Lbf}] {Src} -> {Target}", file.Id, sourceContainer, targetDir);
            }

            try
            {
                Directory.CreateDirectory(targetDir);

                List<string> filesToMove;
                try
                {
                    filesToMove = Directory.EnumerateFiles(
                        sourceContainer, "*", SearchOption.AllDirectories).ToList();
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Series organizer: cannot read source {Path}", sourceContainer);
                    errors++;
                    continue;
                }

                string? newPrimary = null;
                var movedEbooks = new List<string>();
                foreach (var src in filesToMove)
                {
                    var ext = Path.GetExtension(src);

                    // Delete junk files in place rather than propagating them
                    // to the target directory.
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

                PruneEmptyDirs(sourceContainer);
                DeleteEmptyAncestors(sourceContainer, authorDir, targetDir);

                _log.LogInformation("Moved {Count} file(s): {Old} -> {New}",
                    filesToMove.Count, sourceContainer, targetDir);

                var newPath = newPrimary ?? (primaryEbook is null ? targetDir : null);
                if (newPath is not null && !string.Equals(file.FullPath, newPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (pathIndex.TryGetValue(newPath, out var conflictId) && conflictId != file.Id)
                    {
                        _log.LogWarning("Remove stale pointer [{Lbf}]: {Path} (superseded by [{Other}])",
                            file.Id, file.FullPath, conflictId);
                        pathIndex.Remove(file.FullPath);
                        db.LocalBookFiles.Remove(file);
                    }
                    else
                    {
                        pathIndex.Remove(file.FullPath);
                        file.FullPath = newPath;
                        if (newPrimary is not null)
                            file.TitleFolder = Path.GetFileNameWithoutExtension(newPrimary)!;
                        pathIndex[newPath] = file.Id;
                    }
                }

                await db.SaveChangesAsync(ct);
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

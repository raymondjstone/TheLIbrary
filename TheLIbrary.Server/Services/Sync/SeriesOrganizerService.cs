using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.IO;
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
    private readonly IFileSystem _fs;
    private readonly ILogger<SeriesOrganizerService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;

    public SeriesOrganizerService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        IFileSystem fs,
        ILogger<SeriesOrganizerService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _fs = fs;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;

    internal string MoveSingleFileForTests(LocalBookFile file, string targetDir)
    {
        _fs.CreateDirectory(targetDir);
        var destPath = DestinationPath(targetDir, file.FullPath);
        if (!FsPath.SameLocation(file.FullPath, destPath))
            _fs.MoveFile(file.FullPath, destPath, overwrite: false);
        file.FullPath = destPath;
        file.TitleFolder = Path.GetFileNameWithoutExtension(destPath);
        return destPath;
    }

    internal void PruneEmptyDirsForTests(string dir) => PruneEmptyDirs(dir);

    internal void DeleteEmptyAncestorsForTests(string dir, string authorDir, string targetDir)
        => DeleteEmptyAncestors(dir, authorDir, targetDir);

    internal static string? ResolveSeriesNameForTests(string storedSeriesName, int? seriesId, string stem)
    {
        var seriesName = storedSeriesName;
        if (!string.IsNullOrWhiteSpace(seriesName))
        {
            var (cleanedSeries, _, _, _) = TitleNormalizer.TryParseSeriesFilename(seriesName);
            if (!string.IsNullOrWhiteSpace(cleanedSeries)
                && !string.Equals(cleanedSeries, seriesName, StringComparison.OrdinalIgnoreCase))
            {
                return cleanedSeries;
            }
            return seriesName;
        }

        if (seriesName is null && seriesId is null)
        {
            var (parsedSeries, _, _, _) = TitleNormalizer.TryParseSeriesFilename(stem);
            return string.IsNullOrWhiteSpace(parsedSeries) ? null : parsedSeries;
        }

        return null;
    }

    internal static string ComputeTargetDirForTests(string libRoot, string authorFolder, string? seriesName)
    {
        var authorDir = Path.Combine(libRoot.TrimEnd('\\', '/'), authorFolder);
        return string.IsNullOrWhiteSpace(seriesName)
            ? authorDir
            : Path.Combine(authorDir, SanitizeFolderName(seriesName));
    }

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
                finally { _isRunning = false; _currentMessage = null; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        _log.LogInformation("Series organizer starting");
        _currentMessage = "Starting";
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

        // Only organise files that belong to a tracked Book. Unmatched files
        // (no BookId) are the majority of the library; reorganising them by
        // filename-guessed series was the bulk of the work and isn't this job's
        // purpose — the matching/incoming flows own those files. Leaving them out
        // cuts the set from the whole library down to matched books.
        // No tracking: every write in this job goes through ExecuteUpdate/
        // ExecuteDelete, so change-tracking snapshots for 100k+ entities would be
        // pure overhead (and would let an unrelated SaveChanges flush half-mutated
        // records). We mutate the in-memory copies only to keep pathIndex current.
        var files = await db.LocalBookFiles
            .AsNoTracking()
            .Where(f => f.BookId != null)
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

        _log.LogInformation("Series organizer: {Count} matched file(s) to evaluate", files.Count);
        _currentMessage = $"Evaluating {files.Count} file(s)";

        // Conflict index over EVERY record (matched or not), loaded as a
        // lightweight id+path projection — cheap in memory, no per-file disk I/O.
        // This way a matched file's move still detects a path collision with an
        // unmatched record, and directory-record sweeps still skip files owned by
        // unmatched records instead of dragging them along.
        var pathIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in await db.LocalBookFiles.Select(f => new { f.Id, f.FullPath }).ToListAsync(ct))
            pathIndex.TryAdd(f.FullPath, f.Id);

        // Settled-record signatures to persist, flushed in batches so the
        // first run (which fingerprints every folder once) doesn't do 10k+
        // single-row UPDATE round-trips.
        var sigUpdates = new List<(int Id, string Sig)>();

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

            // Settled-record skip — the strongest lever for repeat runs. If we
            // already confirmed THIS record (same path, fingerprint and target)
            // is correctly placed, skip it with ZERO disk access. The signature
            // self-invalidates whenever the scanner re-fingerprints the file, the
            // file moves, or its target series/author changes.
            var organizedSig = ComputeOrganizedSig(file, targetDir);
            if (string.Equals(file.OrganizedSig, organizedSig, StringComparison.Ordinal))
            {
                cntAlreadyCorrect++;
                skipped++;
                continue;
            }

            // Fast path — the dominant cost of this job is a per-file stat on the
            // mounted /Books disk, multiplied by 365k+ records. A record that
            // points to a FILE already sitting directly in its target directory
            // needs neither a move nor any disk access: we can decide that from
            // the stored path string alone and skip, so the run actually finishes
            // (and reaches lower-priority authors) instead of stat-ing every file.
            var ext0 = Path.GetExtension(effectivePath);
            bool isFileRecord = CalibreScanner.EbookExtensions.Contains(ext0, StringComparer.OrdinalIgnoreCase)
                             || CalibreScanner.ArchiveExtensions.Contains(ext0, StringComparer.OrdinalIgnoreCase);
            if (isFileRecord)
            {
                var currentParent = Path.GetDirectoryName(effectivePath);
                if (currentParent is not null && FsPath.SameLocation(currentParent, targetDir))
                {
                    cntAlreadyCorrect++;
                    skipped++;
                    continue;
                }
            }

            string? sourceContainer = null;
            string? primaryEbook = null;
            bool recordIsDirectory = false;
            // Immediate children of a directory record, listed ONCE here and reused
            // for the subdir/flatten check below. ~38% of matched records are classic
            // title/series FOLDERS, and per-record disk I/O on the /Books mount is
            // what makes this job slow — so we probe each path with a single
            // round-trip, ordered by what the stored path looks like.
            string[]? dirEntries = null;

            if (isFileRecord && File.Exists(effectivePath))
            {
                sourceContainer = Path.GetDirectoryName(effectivePath);
                primaryEbook = effectivePath;
            }
            else if (!isFileRecord && (dirEntries = TryListDirectory(effectivePath)) is not null)
            {
                recordIsDirectory = true;
                sourceContainer = effectivePath;
                primaryEbook = PickEbookFromEntries(dirEntries);
            }
            else if (File.Exists(effectivePath))
            {
                // Extensionless file, or one whose extension we don't track.
                sourceContainer = Path.GetDirectoryName(effectivePath);
                primaryEbook = effectivePath;
            }
            else if ((dirEntries = TryListDirectory(effectivePath)) is not null)
            {
                // A file-extension record that is actually a directory on disk.
                recordIsDirectory = true;
                sourceContainer = effectivePath;
                primaryEbook = PickEbookFromEntries(dirEntries);
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
            // Compare by normalized location rather than raw string so separator or
            // normalization differences can never falsely re-trigger a move.
            if (FsPath.SameLocation(sourceContainer, targetDir))
            {
                bool hasSubdirFiles = false;
                // Only look for title-subfolder remnants when the DB record still
                // points to a directory (old Calibre layout). If the record already
                // points to a specific file, there is nothing for THIS record to
                // flatten. recordIsDirectory was determined by the single listing
                // above — no extra stat here.
                if (recordIsDirectory)
                {
                    // Reuse that listing: only pay for the recursive scan when an
                    // entry could be a subfolder (no file extension). A settled
                    // folder of ebook files needs no further disk access at all.
                    bool maybeSubdirs = dirEntries is not null
                        && dirEntries.Any(e => string.IsNullOrEmpty(Path.GetExtension(e)));
                    if (maybeSubdirs)
                    {
                        try
                        {
                            hasSubdirFiles = Directory.EnumerateDirectories(sourceContainer).Any() &&
                                Directory.EnumerateFiles(sourceContainer, "*", SearchOption.AllDirectories)
                                    .Any(f => !string.Equals(
                                        Path.GetDirectoryName(f), sourceContainer,
                                        StringComparison.OrdinalIgnoreCase));
                        }
                        catch { }
                    }
                }

                if (!hasSubdirFiles)
                {
                    var wantPath = primaryEbook ?? effectivePath;
                    if (!FsPath.SameLocation(file.FullPath, wantPath))
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
                    else
                    {
                        // Stays a correctly-placed directory record and nothing
                        // changed — mark it settled so the next run skips it with
                        // no disk access at all (these metadata/empty folders were
                        // the per-run cost that the fast path can't cover).
                        sigUpdates.Add((file.Id, organizedSig));
                        if (sigUpdates.Count >= 2000)
                            await FlushSigUpdatesAsync(db, sigUpdates, ct);
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
                    if (!FsPath.SameLocation(src, destPath))
                        File.Move(src, destPath);

                    if (FsPath.SameLocation(src, primaryEbook))
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
                if (newPath is not null && !FsPath.SameLocation(file.FullPath, newPath))
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
                if (moved % 50 == 0)
                    _currentMessage = $"Moved {moved} file(s) — {skipped} skipped, {errors} error(s)";
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Series organizer: failed to process {Path}", effectivePath);
                errors++;
            }
        }

        await FlushSigUpdatesAsync(db, sigUpdates, ct);

        _log.LogInformation(
            "Series organizer done. Moved={Moved} AlreadyCorrect={AlreadyCorrect} " +
            "NotFound={NotFound} NoLocation={NoLocation} NullContainer={NullContainer} Errors={Errors}",
            moved, cntAlreadyCorrect, cntNotFound, cntNoLocation, cntNullContainer, errors);
        _currentMessage = $"Done — moved {moved}, skipped {skipped}, {errors} error(s)";
    }

    // Stable signature of a record's organised state. Recomputed each run; when
    // it matches the stored value the record is skipped with no disk access. Any
    // change the scanner records (path, fingerprint) or a target-folder change
    // flips the hash, so it can never wrongly skip a record that needs moving.
    private static string ComputeOrganizedSig(LocalBookFile file, string targetDir)
    {
        var raw = $"{file.FullPath}\u0001{file.ModifiedAt.Ticks}\u0001{file.SizeBytes}\u0001{targetDir}";
        var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash);
    }

    // Persists settled-record signatures in batches via a single VALUES join per
    // batch, so a first run over a large library doesn't issue one UPDATE per row.
    private static async Task FlushSigUpdatesAsync(
        LibraryDbContext db, List<(int Id, string Sig)> updates, CancellationToken ct)
    {
        if (updates.Count == 0) return;
        const int batchSize = 700; // 2 params/row, well under SQL Server's 2100 limit
        for (var start = 0; start < updates.Count; start += batchSize)
        {
            var batch = updates.GetRange(start, Math.Min(batchSize, updates.Count - start));
            var sb = new System.Text.StringBuilder(
                "UPDATE lbf SET OrganizedSig = v.Sig FROM LocalBookFiles lbf JOIN (VALUES ");
            var args = new object[batch.Count * 2];
            for (var j = 0; j < batch.Count; j++)
            {
                if (j > 0) sb.Append(',');
                sb.Append(System.Globalization.CultureInfo.InvariantCulture,
                    $"(CAST({{{j * 2}}} AS int), CAST({{{j * 2 + 1}}} AS nvarchar(40)))");
                args[j * 2] = batch[j].Id;
                args[j * 2 + 1] = batch[j].Sig;
            }
            sb.Append(") AS v(Id, Sig) ON lbf.Id = v.Id");
            await db.Database.ExecuteSqlRawAsync(sb.ToString(), args, ct);
        }
        updates.Clear();
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
    private void PruneEmptyDirs(string dir)
    {
        if (!_fs.DirectoryExists(dir)) return;
        try
        {
            foreach (var sub in _fs.EnumerateDirectories(dir))
                PruneEmptyDirs(sub);
            if (!_fs.EnumerateFileSystemEntries(dir).Any())
                _fs.DeleteDirectory(dir);
        }
        catch { }
    }

    // Deletes dir if it is empty and is not authorDir or targetDir, then recurses
    // upward so that intermediate grouping folders (e.g. "Misc" containing title
    // subfolders) are also removed once all their children have been emptied.
    private void DeleteEmptyAncestors(string dir, string authorDir, string targetDir)
    {
        if (string.Equals(dir, authorDir, StringComparison.OrdinalIgnoreCase)) return;
        if (string.Equals(dir, targetDir, StringComparison.OrdinalIgnoreCase)) return;

        var parent = Path.GetDirectoryName(dir);

        if (_fs.DirectoryExists(dir))
        {
            if (_fs.EnumerateFileSystemEntries(dir).Any()) return;
            try { _fs.DeleteDirectory(dir); }
            catch { return; }
        }

        if (parent is not null)
            DeleteEmptyAncestors(parent, authorDir, targetDir);
    }

    // Returns a unique destination path inside targetDir for a file being moved.
    // If targetDir\filename already exists, appends _{n} to the stem.
    private string DestinationPath(string targetDir, string srcFile)
    {
        var name = Path.GetFileName(srcFile);
        var dest = Path.Combine(targetDir, name);
        if (!_fs.FileExists(dest) ||
            string.Equals(dest, srcFile, StringComparison.OrdinalIgnoreCase))
            return dest;

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext  = Path.GetExtension(name);
        for (int n = 1; ; n++)
        {
            dest = Path.Combine(targetDir, $"{stem}_{n}{ext}");
            if (!_fs.FileExists(dest)) return dest;
        }
    }

    // Lists a directory's immediate children in a single round-trip, returning
    // null if the path is not a directory (or can't be read). One disk call that
    // doubles as the existence/type probe AND the content listing.
    private static string[]? TryListDirectory(string path)
    {
        try { return Directory.GetFileSystemEntries(path); }
        catch { return null; }
    }

    // Picks the primary ebook from a pre-read directory listing (epub preferred,
    // then pdf, then any) — no disk access. Subfolders never carry an ebook
    // extension, so filtering the entries by extension is safe.
    private static string? PickEbookFromEntries(IReadOnlyList<string> entries)
    {
        var ebooks = entries
            .Where(f => CalibreScanner.EbookExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .ToList();
        return ebooks.FirstOrDefault(f => Path.GetExtension(f).Equals(".epub", StringComparison.OrdinalIgnoreCase))
            ?? ebooks.FirstOrDefault(f => Path.GetExtension(f).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            ?? ebooks.FirstOrDefault();
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

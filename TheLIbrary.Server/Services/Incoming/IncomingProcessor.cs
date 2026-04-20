using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Services.Incoming;

public sealed record IncomingResult(
    int Processed,
    int Matched,
    int UnknownAuthor,
    int Skipped,
    int Errors,
    IReadOnlyList<string> Log);

public sealed class IncomingProcessor
{
    private readonly LibraryDbContext _db;
    private readonly ILogger<IncomingProcessor> _log;

    public IncomingProcessor(LibraryDbContext db, ILogger<IncomingProcessor> log)
    {
        _db = db; _log = log;
    }

    public Task<IncomingResult> ProcessAsync(CancellationToken ct)
        => ProcessAsync(null, ct);

    public async Task<IncomingResult> ProcessAsync(Action<IncomingProgress>? onProgress, CancellationToken ct)
    {
        var incomingSetting = await _db.AppSettings
            .FirstOrDefaultAsync(s => s.Key == AppSettingKeys.IncomingFolder, ct);
        var incomingPath = incomingSetting?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(incomingPath))
            throw new InvalidOperationException("Incoming folder is not configured.");
        if (!Directory.Exists(incomingPath))
            throw new InvalidOperationException($"Incoming folder does not exist: {incomingPath}");

        var primary = await ResolvePrimaryAsync(ct);
        return await RunAsync(incomingPath, primary.Path, leaveUnmatchedInPlace: false, onProgress, ct);
    }

    // Reprocess the Unknown bucket inside the primary library. Same matching
    // logic as the incoming run; the difference is that files that still don't
    // resolve to an author stay where they are instead of being "moved" back
    // into Unknown (which would be a no-op and would thrash the filesystem).
    public async Task<IncomingResult> ProcessUnknownAsync(Action<IncomingProgress>? onProgress, CancellationToken ct)
    {
        var primary = await ResolvePrimaryAsync(ct);
        var unknownPath = Path.Combine(primary.Path, CalibreScanner.UnknownAuthorFolder);
        if (!Directory.Exists(unknownPath))
            return new IncomingResult(0, 0, 0, 0, 0,
                new[] { $"No '{CalibreScanner.UnknownAuthorFolder}' folder under {primary.Path}" });
        return await RunAsync(unknownPath, primary.Path, leaveUnmatchedInPlace: true, onProgress, ct);
    }

    private async Task<Data.Models.LibraryLocation> ResolvePrimaryAsync(CancellationToken ct)
    {
        var primary = await _db.LibraryLocations
            .FirstOrDefaultAsync(l => l.IsPrimary, ct);
        if (primary is null)
            throw new InvalidOperationException("No primary library location is set.");
        if (!Directory.Exists(primary.Path))
            throw new InvalidOperationException($"Primary location does not exist: {primary.Path}");
        return primary;
    }

    private async Task<IncomingResult> RunAsync(
        string sourcePath,
        string destRoot,
        bool leaveUnmatchedInPlace,
        Action<IncomingProgress>? onProgress,
        CancellationToken ct)
    {
        // Cap the returned log to the most recent N lines so a reprocess over
        // tens of thousands of files doesn't retain (and then deep-copy on
        // every GetState() poll) an ever-growing string list. Matches the
        // 500-line cap IncomingService applies to its live log.
        const int LogCap = 500;
        var log = new List<string>();
        int processed = 0, matched = 0, unknown = 0, skipped = 0, errors = 0;

        // Cache per-run: ancestor folder names → OL author resolution. The
        // folder-layout walk hits the same "Author" folder for every subfolder
        // and every file inside it, so memoizing saves thousands of identical
        // DB round-trips. `null` is a first-class cached value — meaning "we
        // already looked this name up and OL had nothing."
        var olFolderCache = new Dictionary<string, AuthorIndexEntry?>(StringComparer.OrdinalIgnoreCase);

        void Report(string? message, string? line = null)
        {
            if (line is not null)
            {
                log.Add(line);
                if (log.Count > LogCap) log.RemoveRange(0, log.Count - LogCap);
            }
            onProgress?.Invoke(new IncomingProgress(
                processed, matched, unknown, skipped, errors, message, line));
        }

        Report("Loading configuration");

        // Build an in-memory matcher over the watchlist. The full OpenLibrary
        // catalog (millions of rows post-seed) is too big to preload — instead
        // we query it per unmatched file via LookupOpenLibraryAsync below.
        var tracked = await _db.Authors
            .Select(a => new { a.Name, a.CalibreFolderName })
            .ToListAsync(ct);
        var matcher = new AuthorMatcher(tracked.Select(a => new AuthorIndexEntry(
            DisplayName: a.Name,
            FolderName: string.IsNullOrWhiteSpace(a.CalibreFolderName) ? a.Name : a.CalibreFolderName!,
            IsTracked: true)));
        Report($"Indexed {tracked.Count} watchlist authors; scanning {sourcePath}");

        // Stream the tree folder-by-folder instead of enumerating the whole
        // thing up-front. RecurseSubdirectories over SMB is slow and any
        // retryable error in a subtree can stall progress for minutes.
        // Walking top-level directories in isolation means each folder's
        // files are visible to processing as soon as they're listed.
        int scannedDirs = 0;
        void OnDirVisited(string visitedDir)
        {
            scannedDirs++;
            // Even folders with no book files get reported so the UI sees
            // progress on large or sparsely-populated trees. Keep the message
            // tight — it's overwritten several times per second on SMB walks.
            Report($"Scanned {scannedDirs} folder(s) — {visitedDir}");
        }

        foreach (var (dir, filesInDir) in EnumerateByFolder(sourcePath, OnDirVisited))
        {
            ct.ThrowIfCancellationRequested();
            Report($"Processing {dir} ({filesInDir.Count} file(s))");

            // Wrap the whole per-folder body so a failure isolated to one
            // folder (e.g. an unreadable file, a race with another writer,
            // a weird permission blip) doesn't abort the entire run.
            try
            {
            // Covers aren't books — delete them immediately.
            var remaining = new List<string>(filesInDir.Count);
            foreach (var f in filesInDir)
            {
                var fext = Path.GetExtension(f).ToLowerInvariant();
                if (fext is ".jpg" or ".jpeg")
                {
                    try { DeleteAndWait(f); Report(null, $"deleted cover: {f}"); }
                    catch (Exception ex) { errors++; Report(null, $"error deleting {f}: {ex.Message}"); }
                }
                else remaining.Add(f);
            }

            var opfPath = remaining.FirstOrDefault(f =>
                string.Equals(Path.GetFileName(f), "metadata.opf", StringComparison.OrdinalIgnoreCase));
            var books = remaining.Where(f => !ReferenceEquals(f, opfPath)).ToList();

            // Only trust the opf when it can only be describing one book.
            var useOpf = books.Count == 1 && opfPath is not null;

            // Folder-level Calibre-layout resolution: if any ancestor of THIS
            // folder (inclusive of itself) matches a tracked author, we treat
            // the whole folder as "<Author>/<Title>/<files>" and keep every
            // file in it together. Prevents split-across-folders when some
            // files in a title folder have readable metadata and others don't.
            var (folderEntry, folderTitle) = matcher.ResolveFolderLayout(dir, sourcePath);

            // If the tracked watchlist didn't cover the ancestor names, ask
            // the OpenLibrary catalog. Covers the common case of a drop
            // folder literally named after a known author whose books all
            // have empty / unreadable metadata (no per-file signal to probe).
            if (folderEntry is null)
            {
                (folderEntry, folderTitle) = await ResolveFolderLayoutViaOpenLibraryAsync(
                    dir, sourcePath, olFolderCache, ct);
            }

            var allMoved = true;
            foreach (var file in books)
            {
                ct.ThrowIfCancellationRequested();
                processed++;
                try
                {
                    var meta = ReadMetadata(file);
                    if (useOpf && NeedsMore(meta))
                    {
                        var opf = OpfMetadataReader.TryReadFile(opfPath!);
                        if (opf is not null) meta = Merge(meta, opf);
                    }

                    AuthorIndexEntry? matchedEntry = null;
                    string? rewrittenTitle = null;
                    var usedFolderLayout = false;

                    var primary = matcher.Resolve(meta?.Author, meta?.AuthorSort, file);
                    if (primary is not null)
                    {
                        matchedEntry = primary.Entry;
                        rewrittenTitle = primary.RewrittenTitle;
                    }
                    else if (folderEntry is not null)
                    {
                        // Files with no readable in-file metadata (e.g. .lit,
                        // corrupt epub, bare pdf) benefit from the ancestor-
                        // folder signal when it matches either a tracked
                        // author or an OpenLibrary catalog entry.
                        matchedEntry = folderEntry;
                        usedFolderLayout = true;
                    }
                    else
                    {
                        // Final fallback: the OpenLibrary author catalog. The
                        // dump covers every OL author and the same normalized
                        // key space that the matcher uses for tracked authors,
                        // so a file whose author isn't on our watchlist yet can
                        // still be identified. We still route it under
                        // __unknown/<AuthorName>/ so a later reprocess-unknown
                        // picks it up once the user promotes that author.
                        var olMatch = await LookupOpenLibraryAsync(matcher, meta, file, ct);
                        if (olMatch is not null)
                        {
                            matchedEntry = olMatch.Entry;
                            rewrittenTitle = olMatch.RewrittenTitle;
                        }
                    }

                    string destDir;
                    if (matchedEntry is null)
                    {
                        // In reprocess-unknown mode, leaving a still-unmatched
                        // file in place is the whole point — skip instead of
                        // "moving" it right back where it already is.
                        if (leaveUnmatchedInPlace)
                        {
                            unknown++;
                            allMoved = false;
                            Report(null, $"still unmatched: {file}");
                            continue;
                        }
                        unknown++;

                        // Don't invent a title folder from metadata when we
                        // couldn't match the author — bad metadata produced
                        // garbage names like "DH___" under __unknown. Mirror
                        // the file's relative path from the source root so
                        // a later reprocess-unknown can still pick up any
                        // folder-layout signal the user has in place.
                        var rel = Path.GetRelativePath(sourcePath, dir);
                        destDir = string.IsNullOrEmpty(rel) || rel == "."
                            ? Path.Combine(destRoot, CalibreScanner.UnknownAuthorFolder)
                            : Path.Combine(destRoot, CalibreScanner.UnknownAuthorFolder, rel);
                    }
                    else
                    {
                        matched++;

                        // When the author came from the folder layout, preserve
                        // the existing title-folder name so multi-format books
                        // (epub + mobi + lit + ...) stay in one place. Prefer
                        // the reverse-filename rewrite when that's where the
                        // match came from; otherwise trust metadata.
                        var titleFolder = usedFolderLayout && folderTitle is not null
                            ? Sanitize(folderTitle) ?? Sanitize(meta?.Title)
                            : Sanitize(rewrittenTitle) ?? Sanitize(meta?.Title) ?? Sanitize(folderTitle);
                        titleFolder ??= Sanitize(Path.GetFileNameWithoutExtension(file)) ?? "Untitled";

                        // Matched files — tracked OR OL — go straight to the
                        // collection's author folder. OL-only matches still
                        // surface as "unclaimed" in the UI so the user can
                        // promote the author in one click; routing them back
                        // under __unknown would just churn the filesystem on
                        // every reprocess-unknown pass.
                        var authorSegment = Sanitize(matchedEntry.FolderName) ?? CalibreScanner.UnknownAuthorFolder;
                        destDir = Path.Combine(destRoot, authorSegment, titleFolder);
                    }
                    Directory.CreateDirectory(destDir);
                    var destPath = Path.Combine(destDir, Path.GetFileName(file));

                    // If the computed destination IS the current file location
                    // there's nothing to do — common during reprocess-unknown
                    // when folderAuthor resolves to the same path the file is
                    // already sitting under.
                    if (string.Equals(
                            Path.GetFullPath(destPath),
                            Path.GetFullPath(file),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        skipped++;
                        Report(null, $"skip (already in place): {file}");
                        continue;
                    }

                    if (File.Exists(destPath))
                    {
                        // Don't silently overwrite an existing copy — leaves
                        // the source file behind so the user can reconcile.
                        skipped++;
                        allMoved = false;
                        Report(null, $"skip (exists at dest): {file} → {destPath}");
                        continue;
                    }

                    MoveAndWait(file, destPath, overwrite: false);
                    Report($"Moved {Path.GetFileName(file)} → {matchedEntry!.FolderName}",
                        $"moved: {file} → {destPath}");
                }
                catch (Exception ex)
                {
                    errors++;
                    allMoved = false;
                    Report(null, $"error: {file} — {ex.Message}");
                    _log.LogWarning(ex, "Failed to process incoming file {File}", file);
                }
            }

            // Drop the opf when it's either orphaned (no book siblings) or its
            // book siblings were all successfully moved out. If any book move
            // failed we leave the opf in place so a retry can still use it.
            if (opfPath is not null && File.Exists(opfPath)
                && (books.Count == 0 || allMoved))
            {
                try { DeleteAndWait(opfPath); Report(null, $"deleted opf: {opfPath}"); }
                catch (Exception ex) { errors++; Report(null, $"error deleting {opfPath}: {ex.Message}"); }
            }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errors++;
                _log.LogWarning(ex, "Folder processing failed for {Dir}", dir);
                Report(null, $"error processing folder {dir}: {ex.Message}");
            }
        }

        // Tidy up: drop any empty subfolders left behind under the source root.
        Report("Cleaning up empty folders");
        CleanupEmptyDirs(sourcePath, line => Report(null, line));
        Report("Complete");

        return new IncomingResult(processed, matched, unknown, skipped, errors, log);
    }

    private static bool NeedsMore(EpubMetadata? m)
        => m is null || string.IsNullOrWhiteSpace(m.Author) || string.IsNullOrWhiteSpace(m.Title);

    // Prefer existing (in-file) values; fill blanks from the sidecar. Author is
    // the one we care most about for routing, so fall back to the opf whenever
    // the book file didn't give us one.
    private static EpubMetadata Merge(EpubMetadata? a, EpubMetadata b) => new(
        !string.IsNullOrWhiteSpace(a?.Title) ? a!.Title : b.Title,
        !string.IsNullOrWhiteSpace(a?.Author) ? a!.Author : b.Author,
        !string.IsNullOrWhiteSpace(a?.AuthorSort) ? a!.AuthorSort : b.AuthorSort,
        !string.IsNullOrWhiteSpace(a?.Language) ? a!.Language : b.Language);

    // Walks ancestors of folderPath upward (excluding sourceRoot and the
    // __unknown quarantine folder) and asks the OpenLibrary catalog about
    // each name. First hit wins; the result is treated as an OL-only match
    // (IsTracked=false) so the caller routes files under
    // __unknown/<AuthorName>/<Title>/. Results are cached per-run so a folder
    // with 200 subdirectories under one author only triggers one DB query.
    private async Task<(AuthorIndexEntry? Entry, string? Title)> ResolveFolderLayoutViaOpenLibraryAsync(
        string folderPath,
        string sourceRoot,
        Dictionary<string, AuthorIndexEntry?> cache,
        CancellationToken ct)
    {
        var root = Path.TrimEndingDirectorySeparator(sourceRoot);
        var current = Path.TrimEndingDirectorySeparator(folderPath);
        string? nearestBelow = null;

        while (!string.IsNullOrWhiteSpace(current)
            && !string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(current);
            if (!string.IsNullOrWhiteSpace(name)
                && !string.Equals(name, CalibreScanner.UnknownAuthorFolder, StringComparison.OrdinalIgnoreCase))
            {
                if (!cache.TryGetValue(name, out var cached))
                {
                    cached = null;
                    foreach (var probe in AuthorMatcher.AuthorKeyVariants(name))
                    {
                        var hit = await _db.OpenLibraryAuthors
                            .AsNoTracking()
                            .Where(a => a.NormalizedName == probe)
                            .OrderBy(a => a.Id)
                            .Select(a => new { a.Name })
                            .FirstOrDefaultAsync(ct);
                        if (hit is not null)
                        {
                            cached = new AuthorIndexEntry(hit.Name, hit.Name, IsTracked: false);
                            break;
                        }
                    }
                    cache[name] = cached;
                }

                if (cached is not null) return (cached, nearestBelow);
                nearestBelow = name;
            }
            current = Path.GetDirectoryName(current);
        }
        return (null, null);
    }

    // Queries the OpenLibrary author catalog with the same probe keys the
    // in-memory matcher already tried. Tracked authors always win, so we only
    // reach here on a miss; an OL hit gets wrapped as an untracked entry so
    // the caller routes it to __unknown/<AuthorName>/.
    private async Task<AuthorMatchResult?> LookupOpenLibraryAsync(
        AuthorMatcher matcher, EpubMetadata? meta, string file, CancellationToken ct)
    {
        foreach (var (key, rewrittenTitle) in matcher.GetProbeKeys(meta?.Author, meta?.AuthorSort, file))
        {
            var hit = await _db.OpenLibraryAuthors
                .AsNoTracking()
                .Where(a => a.NormalizedName == key)
                .OrderBy(a => a.Id)
                .Select(a => new { a.Name })
                .FirstOrDefaultAsync(ct);
            if (hit is not null)
            {
                return new AuthorMatchResult(
                    new AuthorIndexEntry(hit.Name, hit.Name, IsTracked: false),
                    rewrittenTitle);
            }
        }
        return null;
    }

    private EpubMetadata? ReadMetadata(string file)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        EpubMetadata? m = ext switch
        {
            ".epub" => EpubMetadataReader.TryReadFile(file),
            ".fb2" or ".fbz" => Fb2MetadataReader.TryReadFile(file),
            ".zip" when file.EndsWith(".fb2.zip", StringComparison.OrdinalIgnoreCase)
                => Fb2MetadataReader.TryReadFile(file),
            ".mobi" or ".azw" or ".azw3" or ".azw4" or ".kf8" or ".prc" or ".pdb"
                => MobiMetadataReader.TryReadFile(file),
            ".pdf" => PdfMetadataReader.TryReadFile(file, _log),
            ".lit" => LitMetadataReader.TryReadFile(file),
            ".cbz" => CbzMetadataReader.TryReadFile(file),
            ".docx" or ".odt" => DocxMetadataReader.TryReadFile(file),
            _ => null
        };
        if (m is not null && (m.Title is not null || m.Author is not null)) return m;

        // Fallback: infer from the filename. "Author - Title.ext" is the common
        // Library Genesis / calibre-plugin-export convention.
        var stem = Path.GetFileNameWithoutExtension(file);
        var dash = stem.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
        {
            var author = stem[..dash].Trim();
            var title = stem[(dash + 3)..].Trim();
            return new EpubMetadata(title, author, null, m?.Language);
        }
        return new EpubMetadata(stem, null, null, m?.Language);
    }

    // Top-down breadth-first walk yielding one folder's direct-child files
    // at a time. Each subdirectory is listed independently with
    // RecurseSubdirectories=false, so an error on one subtree only costs that
    // subtree instead of aborting or retrying the whole enumeration. The
    // caller can process files while the walk continues, so work begins
    // immediately rather than after a (possibly very long) full traversal.
    private IEnumerable<(string Dir, List<string> Files)> EnumerateByFolder(string root, Action<string>? onDirVisited = null)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        var queue = new Queue<string>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var dir = queue.Dequeue();
            onDirVisited?.Invoke(dir);

            List<string> files;
            try { files = Directory.EnumerateFiles(dir, "*", opts).ToList(); }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Skipping unreadable files in {Dir}", dir);
                files = new List<string>();
            }

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir, "*", opts))
                    queue.Enqueue(sub);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Skipping unreadable subdirs of {Dir}", dir);
            }

            if (files.Count > 0) yield return (dir, files);
        }
    }

    // Synchronous file ops + a short settle loop. On network shares (SMB /
    // OneDrive) File.Move and File.Delete can return before the change is
    // visible to subsequent calls, and AV / indexers often hold a transient
    // handle after we close our own — retry with backoff so we don't proceed
    // while a handle is still open on the previous file.
    private static void DeleteAndWait(string path)
    {
        const int maxAttempts = 6;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                for (var i = 0; i < 20 && File.Exists(path); i++) Thread.Sleep(50);
                if (!File.Exists(path)) return;
            }
            catch (IOException) when (attempt < maxAttempts) { }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts) { }
            Thread.Sleep(200 * attempt);
        }
        // Final attempt without swallowing — let the caller log the failure.
        File.Delete(path);
    }

    private static void MoveAndWait(string src, string dst, bool overwrite)
    {
        const int maxAttempts = 6;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                File.Move(src, dst, overwrite);
                for (var i = 0; i < 20 && (File.Exists(src) || !File.Exists(dst)); i++)
                    Thread.Sleep(50);
                if (!File.Exists(src) && File.Exists(dst)) return;
            }
            catch (IOException) when (attempt < maxAttempts) { }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts) { }
            Thread.Sleep(200 * attempt);
        }
        File.Move(src, dst, overwrite);
    }

    // Remove empty directories bottom-up so a drained incoming folder doesn't
    // leave clutter behind. Recurses depth-first and only evaluates a folder
    // after its children have been processed, so chains of nested-empty
    // folders collapse in one pass. Doesn't touch the root itself.
    private static void CleanupEmptyDirs(string root, Action<string> emit)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        CleanDir(root, normalizedRoot, emit);
    }

    private static void CleanDir(string dir, string root, Action<string> emit)
    {
        List<string> subs;
        try { subs = Directory.EnumerateDirectories(dir).ToList(); }
        catch { return; }

        foreach (var sub in subs)
            CleanDir(sub, root, emit);

        if (string.Equals(
                Path.TrimEndingDirectorySeparator(dir),
                root,
                StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
                emit($"removed empty dir: {dir}");
            }
        }
        catch { /* best effort */ }
    }

    private static readonly HashSet<char> Invalid =
        new(Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()));

    private static string? Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        // Drop invalid filename chars, ASCII control chars, and Unicode
        // format/control characters (category Cf/Cc — BiDi marks etc.) which
        // NTFS accepts in some contexts but CreateDirectory rejects.
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (Invalid.Contains(c)) { sb.Append('_'); continue; }
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat is System.Globalization.UnicodeCategory.Control
                    or System.Globalization.UnicodeCategory.Format) continue;
            sb.Append(c);
        }
        var clean = sb.ToString().Trim().TrimEnd('.', ' ');
        // Windows path components cap at 255, full paths at 260 without long
        // path support. Cap components well below that so the parent + file
        // name still fit.
        const int MaxLen = 120;
        if (clean.Length > MaxLen) clean = clean[..MaxLen].TrimEnd('.', ' ');
        return clean.Length == 0 ? null : clean;
    }
}

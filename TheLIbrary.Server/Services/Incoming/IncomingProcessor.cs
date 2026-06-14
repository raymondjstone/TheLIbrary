using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.IO;
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
    private readonly IFileSystem _fs;
    private readonly ILogger<IncomingProcessor> _log;

    public IncomingProcessor(LibraryDbContext db, IFileSystem fs, ILogger<IncomingProcessor> log)
    {
        _db = db; _fs = fs; _log = log;
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
        if (!_fs.DirectoryExists(incomingPath))
            throw new InvalidOperationException($"Incoming folder does not exist: {incomingPath}");

        var primary = await ResolvePrimaryAsync(ct);
        return await RunAsync(incomingPath, primary.Path, leaveUnmatchedInPlace: false, autoAddOlAuthors: false, onProgress, ct);
    }

    // Reprocess the Unknown bucket inside the primary library. Same matching
    // logic as the incoming run; the difference is that files that still don't
    // resolve to an author stay where they are instead of being "moved" back
    // into Unknown (which would be a no-op and would thrash the filesystem).
    // autoAddOlAuthors=true so any OL-matched author that isn't on the
    // watchlist yet is auto-created as a Pending row and their files moved
    // immediately instead of staying quarantined forever.
    public async Task<IncomingResult> ProcessUnknownAsync(Action<IncomingProgress>? onProgress, CancellationToken ct)
    {
        var primary = await ResolvePrimaryAsync(ct);
        var unknownPath = await UnknownFolderResolver.GetDestinationRootAsync(_db, primary.Path, ct);
        if (!_fs.DirectoryExists(unknownPath))
            return new IncomingResult(0, 0, 0, 0, 0,
                new[] { $"No quarantine folder found at {unknownPath}" });
        return await RunAsync(unknownPath, primary.Path, leaveUnmatchedInPlace: true, autoAddOlAuthors: true, onProgress, ct);
    }

    private async Task<Data.Models.LibraryLocation> ResolvePrimaryAsync(CancellationToken ct)
    {
        var primary = await _db.LibraryLocations
            .FirstOrDefaultAsync(l => l.IsPrimary, ct);
        if (primary is null)
            throw new InvalidOperationException("No primary library location is set.");
        if (!_fs.DirectoryExists(primary.Path))
            throw new InvalidOperationException($"Primary location does not exist: {primary.Path}");
        return primary;
    }

    private async Task<IncomingResult> RunAsync(
        string sourcePath,
        string destRoot,
        bool leaveUnmatchedInPlace,
        bool autoAddOlAuthors,
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

        // Blacklist is the "this author is banished" list — populated when the
        // user deletes an author from the watchlist. We feed it both to the
        // matcher (so blacklisted entries are never indexed) and to the OL
        // lookup paths (so a catalog hit that happens to name a banished
        // author is ignored and the file goes to __unknown instead).
        var blacklistedNormalized = await _db.AuthorBlacklist
            .AsNoTracking()
            .Select(b => b.NormalizedName)
            .ToListAsync(ct);
        var blacklistSet = new HashSet<string>(blacklistedNormalized, StringComparer.Ordinal);

        // Build an in-memory matcher over the watchlist. The full OpenLibrary
        // catalog (millions of rows post-seed) is too big to preload — instead
        // we query it per unmatched file via LookupOpenLibraryAsync below.
        // Only non-Excluded authors are indexed — Excluded authors must not grab
        // their normalized-name key and block Active rows with the same name.
        // NOTE: do NOT add Excluded author names to blacklistSet — many Excluded
        // rows are OL duplicates of Active authors, and blacklisting those names
        // would prevent the Active rows from ever matching.
        var tracked = await _db.Authors
            .Where(a => a.Status != AuthorStatus.Excluded)
            .Select(a => new { a.Id, a.Name, a.CalibreFolderName, a.OpenLibraryKey })
            .ToListAsync(ct);

        var matcher = new AuthorMatcher(
            tracked.Select(a => new AuthorIndexEntry(
                DisplayName: a.Name,
                FolderName: string.IsNullOrWhiteSpace(a.CalibreFolderName) ? a.Name : a.CalibreFolderName!,
                IsTracked: true,
                TrackedAuthorId: a.Id,
                OpenLibraryKey: a.OpenLibraryKey)),
            blacklistedNormalized);
        Report($"Indexed {tracked.Count} watchlist authors, {blacklistSet.Count} blacklisted; scanning {sourcePath}");

        // Reprocess-unknown only: the same author name appearing on MULTIPLE
        // distinct files is corroboration in itself ("A Most Unusual Duke -
        // Felicia Greene.azw3" + "A Most Unusual Earl - Felicia Greene.azw3"),
        // which rescues the DRM'd/metadata-less files whose only signal is the
        // filename. Counted up-front over the whole tree (names only — no file
        // reads); used as the very last resolution tier, vetoed by known
        // SERIES names (a series suffix repeats across files exactly like an
        // author would) and the blacklist.
        var repeatedNameKeys = new Dictionary<string, int>(StringComparer.Ordinal);
        if (autoAddOlAuthors)
        {
            Report("Counting repeated filename author candidates");
            var seriesVeto = (await _db.Series.AsNoTracking().Select(s => s.Name).ToListAsync(ct))
                .SelectMany(AuthorMatcher.AuthorKeyVariants)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var (_, names) in EnumerateByFolder(sourcePath))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var f in names)
                {
                    var perFileKeys = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var g in FilenameGuesser.Interpret(f))
                    {
                        if (!LooksLikePersonalName(g.Author)) continue;
                        var key = TitleNormalizer.NormalizeAuthor(g.Author!);
                        if (key.Length == 0 || blacklistSet.Contains(key) || seriesVeto.Contains(key)) continue;
                        perFileKeys.Add(key); // one vote per file, however many guesses agree
                    }
                    foreach (var key in perFileKeys)
                        repeatedNameKeys[key] = repeatedNameKeys.GetValueOrDefault(key) + 1;
                }
            }
            repeatedNameKeys = repeatedNameKeys
                .Where(kv => kv.Value >= 2)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            Report($"Found {repeatedNameKeys.Count} author name(s) repeated across files");
        }

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
            // Extract any .zip/.rar archives inline so their contents get
            // matched in the same pass. The extracted files land back in the
            // same folder so the folder-layout author signal is preserved.
            // After extraction the archive itself is deleted.
            var allFiles = new List<string>(filesInDir);
            var knownPaths = new HashSet<string>(filesInDir, StringComparer.OrdinalIgnoreCase);
            foreach (var f in filesInDir)
            {
                if (!CalibreScanner.ArchiveExtensions.Contains(
                        Path.GetExtension(f).ToLowerInvariant()))
                    continue;
                try
                {
                    Sync.UnzipService.ExtractArchive(f, dir);
                    DeleteAndWait(f);
                    allFiles.Remove(f);
                    var opts = new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint
                    };
                    var extracted = _fs.EnumerateFiles(dir, "*", opts)
                        .Where(e => !knownPaths.Contains(e))
                        .ToList();
                    foreach (var e in extracted) knownPaths.Add(e);
                    allFiles.AddRange(extracted);
                    Report(null, $"extracted archive: {f} ({extracted.Count} file(s))");
                }
                catch (Exception ex)
                {
                    errors++;
                    Report(null, $"error extracting {f}: {ex.Message}");
                }
            }

            // Covers and junk files aren't books — delete them immediately.
            var remaining = new List<string>(allFiles.Count);
            foreach (var f in allFiles)
            {
                var fext = Path.GetExtension(f).ToLowerInvariant();
                var fname = Path.GetFileName(f);
                if (fext is ".jpg" or ".jpeg"
                    || CalibreScanner.JunkExtensions.Contains(fext)
                    || CalibreScanner.JunkFileNames.Contains(fname))
                {
                    try { DeleteAndWait(f); Report(null, $"deleted junk: {f}"); }
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
                    dir, sourcePath, olFolderCache, blacklistSet, ct);
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
                        // still be identified.
                        var olMatch = await LookupOpenLibraryAsync(matcher, meta, file, blacklistSet, ct);
                        if (olMatch is not null)
                        {
                            matchedEntry = olMatch.Entry;
                            rewrittenTitle = olMatch.RewrittenTitle;
                        }
                        else if (autoAddOlAuthors)
                        {
                            // Last resort, reprocess-unknown only. Measured over
                            // the live quarantine, the BULK of the leftover files
                            // are KU/indie books whose author parses perfectly
                            // from the name ("A Most Unusual Duke - Felicia
                            // Greene.azw3") but who simply isn't in the OL dump,
                            // so catalogue validation could never pass. When the
                            // file's EMBEDDED metadata and its FILENAME
                            // independently agree on the same plausible personal
                            // name, that corroboration stands in for the
                            // catalogue: the author is created as Pending.
                            var corroborated = FindCorroboratedAuthor(
                                FileMetadataReader.TryRead(file, _log), file, blacklistSet);
                            if (corroborated is not null)
                            {
                                matchedEntry = new AuthorIndexEntry(
                                    DisplayName: corroborated.Value.Author,
                                    FolderName: corroborated.Value.Author,
                                    IsTracked: false,
                                    TrackedAuthorId: null,
                                    OpenLibraryKey: null);
                                rewrittenTitle = corroborated.Value.Title;
                            }
                            else
                            {
                                // Metadata unreadable (DRM'd azw3/mobi) — but a
                                // name this file shares with at least one OTHER
                                // file is corroborated by repetition instead.
                                foreach (var g in FilenameGuesser.Interpret(file))
                                {
                                    if (!LooksLikePersonalName(g.Author)) continue;
                                    var key = TitleNormalizer.NormalizeAuthor(g.Author!);
                                    if (!repeatedNameKeys.ContainsKey(key)) continue;
                                    matchedEntry = new AuthorIndexEntry(
                                        DisplayName: g.Author!,
                                        FolderName: g.Author!,
                                        IsTracked: false,
                                        TrackedAuthorId: null,
                                        OpenLibraryKey: null);
                                    rewrittenTitle = g.Title;
                                    break;
                                }
                            }
                        }
                    }

                    // Resolve the match through ResolveOrCreateAuthorAsync:
                    //   - Tracked author: always routes to their folder (OL key
                    //     backfilled opportunistically but not required).
                    //   - OL-only match: upsert a Pending Author row first.
                    //   - Returns null only for Excluded / blacklisted authors or
                    //     OL-matched-but-not-on-watchlist (user must add them).
                    var identifiedButUntracked = (string?)null;
                    if (matchedEntry is not null)
                    {
                        var before = matchedEntry;
                        matchedEntry = await ResolveOrCreateAuthorAsync(matchedEntry, blacklistSet, autoAddOlAuthors, ct);
                        if (matchedEntry is null) identifiedButUntracked = before.DisplayName;
                    }

                    // Final guard: even after OL verification, reject any folder name
                    // that doesn't look like a real author name (version strings like
                    // "3.9", bracket-decorated names like "[美]Jeff Johnson", etc.).
                    if (matchedEntry is not null && !TitleNormalizer.IsPlausibleAuthorName(matchedEntry.FolderName))
                    {
                        _log.LogWarning(
                            "Rejecting implausible author folder name '{Name}' — routing to __unknown",
                            matchedEntry.FolderName);
                        matchedEntry = null;
                    }

                    string destDir;
                    if (matchedEntry is null)
                    {
                        // In reprocess-unknown mode, leaving a still-unmatched
                        // file in place is the whole point — skip instead of
                        // "moving" it right back where it already is. Say WHY it
                        // stayed: an identified-but-untracked author is the
                        // watchlist policy at work (add the author, or use the
                        // Identified page's assign flow), not a parsing failure.
                        if (leaveUnmatchedInPlace)
                        {
                            unknown++;
                            allMoved = false;
                            // During reprocess (autoAddOlAuthors=true) an OL-catalogue
                            // author would have been auto-added, so a refusal here can
                            // ONLY mean the author is excluded or blacklisted — say
                            // that, not the misleading "not on watchlist" (which is
                            // only the correct reason for a regular incoming run).
                            Report(null, identifiedButUntracked is null
                                ? $"still unmatched: {file}"
                                : autoAddOlAuthors
                                    ? $"author identified ({identifiedButUntracked}) but excluded or blacklisted — left in unknown: {file}"
                                    : $"author identified ({identifiedButUntracked}) but not on watchlist: {file}");
                            continue;
                        }
                        unknown++;

                        // All unmatched files go flat to the unknown root —
                        // no author-hint subfolders. The reprocess-unknown job
                        // re-scans the root directly, so nested folders add no
                        // value and make the quarantine folder harder to browse.
                        var unknownDestRoot = await UnknownFolderResolver.GetDestinationRootAsync(_db, destRoot, ct);
                        destDir = unknownDestRoot;
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

                        // matchedEntry has been OL-verified and its Author row
                        // exists in the DB — safe to create the collection
                        // folder under its CalibreFolderName.
                        var authorSegment = Sanitize(matchedEntry.FolderName) ?? CalibreScanner.UnknownAuthorFolder;
                        destDir = Path.Combine(destRoot, authorSegment, titleFolder);
                    }
                    _fs.CreateDirectory(destDir);
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

                    // Unknown route only: collapsing source subfolders means two
                    // different folders can carry the same filename — suffix the
                    // newcomer instead of overwriting a different book. Matched
                    // routes keep overwrite semantics (re-import of same title).
                    if (matchedEntry is null && _fs.FileExists(destPath))
                        destPath = UniqueDestPath(destPath);

                    MoveAndWait(file, destPath, overwrite: _fs.FileExists(destPath));
                    var destFolderLabel = matchedEntry?.FolderName ?? CalibreScanner.UnknownAuthorFolder;
                    Report($"Moved {Path.GetFileName(file)} → {destFolderLabel}",
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
            if (opfPath is not null && _fs.FileExists(opfPath)
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
        !string.IsNullOrWhiteSpace(a?.Language) ? a!.Language : b.Language,
        a?.Subject ?? b.Subject,
        a?.Series ?? b.Series,
        a?.SeriesPosition ?? b.SeriesPosition);

    // Walks ancestors of folderPath upward (excluding sourceRoot and the
    // __unknown quarantine folder) and asks the OpenLibrary catalog about
    // each name. First hit wins; the result is treated as an OL-only match
    // (IsTracked=false) so the caller routes files under
    // __unknown/<AuthorName>/. Results are cached per-run so a folder
    // with 200 subdirectories under one author only triggers one DB query.
    private async Task<(AuthorIndexEntry? Entry, string? Title)> ResolveFolderLayoutViaOpenLibraryAsync(
        string folderPath,
        string sourceRoot,
        Dictionary<string, AuthorIndexEntry?> cache,
        HashSet<string> blacklist,
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
                // Don't even ask OL about folders whose name normalizes to a
                // blacklisted author — that's the whole point of the list.
                var nameKey = TitleNormalizer.NormalizeAuthor(name);
                if (blacklist.Contains(nameKey))
                {
                    cache[name] = null;
                    nearestBelow = name;
                    current = Path.GetDirectoryName(current);
                    continue;
                }

                if (!cache.TryGetValue(name, out var cached))
                {
                    cached = null;
                    foreach (var probe in AuthorMatcher.AuthorKeyVariants(name))
                    {
                        var hit = await _db.OpenLibraryAuthors
                            .AsNoTracking()
                            .Where(a => a.NormalizedName == probe)
                            .OrderBy(a => a.Id)
                            .Select(a => new { a.Name, a.OlKey })
                            .FirstOrDefaultAsync(ct);
                        if (hit is not null)
                        {
                            // OL knew about this name but the user banished
                            // them — treat as miss so the file lands in
                            // __unknown exactly as the user asked.
                            var hitKey = TitleNormalizer.NormalizeAuthor(hit.Name);
                            if (!blacklist.Contains(hitKey))
                            {
                                cached = new AuthorIndexEntry(
                                    DisplayName: hit.Name,
                                    FolderName: hit.Name,
                                    IsTracked: false,
                                    TrackedAuthorId: null,
                                    OpenLibraryKey: hit.OlKey);
                                break;
                            }
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
        AuthorMatcher matcher, EpubMetadata? meta, string file, HashSet<string> blacklist, CancellationToken ct)
    {
        foreach (var (key, rewrittenTitle) in matcher.GetProbeKeys(meta?.Author, meta?.AuthorSort, file))
        {
            // Short-circuit on blacklisted probe keys — the user doesn't want
            // this author resurrected even if OL knows them.
            if (blacklist.Contains(key)) continue;

            var hit = await _db.OpenLibraryAuthors
                .AsNoTracking()
                .Where(a => a.NormalizedName == key)
                .OrderBy(a => a.Id)
                .Select(a => new { a.Name, a.OlKey })
                .FirstOrDefaultAsync(ct);
            if (hit is not null)
            {
                var hitKey = TitleNormalizer.NormalizeAuthor(hit.Name);
                if (blacklist.Contains(hitKey)) continue;
                return new AuthorMatchResult(
                    new AuthorIndexEntry(
                        DisplayName: hit.Name,
                        FolderName: hit.Name,
                        IsTracked: false,
                        TrackedAuthorId: null,
                        OpenLibraryKey: hit.OlKey),
                    rewrittenTitle);
            }
        }
        return null;
    }

    // Guarantees the matched entry is backed by a real Author row with an OL
    // key before any collection folder is created for it:
    //   - Tracked with OL key: use as-is.
    //   - Tracked without OL key: try to resolve one from the OL catalog by
    //     name; on hit, persist it so future runs skip this step; on miss,
    //     downgrade to null (route file to __unknown).
    //   - OL-only match: upsert a Pending Author row with the OL key so the
    //     folder we're about to create has a DB owner immediately.
    // Returns an entry re-stamped with the resolved author's id/folder name
    // (IsTracked=true), or null if the match couldn't be OL-verified. The
    // returned FolderName is what the caller uses as the on-disk author
    // folder segment — so it's always a real, OL-backed author name.
    private async Task<AuthorIndexEntry?> ResolveOrCreateAuthorAsync(
        AuthorIndexEntry entry, HashSet<string> blacklistSet, bool autoAddOlAuthors, CancellationToken ct)
    {
        if (entry.IsTracked && entry.TrackedAuthorId is int trackedId)
        {
            // An Excluded author must not attract files during a regular incoming
            // run. During reprocess-unknown (autoAddOlAuthors=true) the file was
            // identified from its own content — un-exclude the author and proceed
            // so the file is delivered instead of left in quarantine indefinitely.
            var trackedAuthor = await _db.Authors.FirstOrDefaultAsync(a => a.Id == trackedId, ct);
            if (trackedAuthor is null) return null;
            if (trackedAuthor.Status == AuthorStatus.Excluded)
            {
                if (!autoAddOlAuthors) return null;
                _log.LogInformation(
                    "Auto-lifting exclusion on author '{Name}' — identified from file content during unknown reprocess",
                    trackedAuthor.Name);
                trackedAuthor.Status = AuthorStatus.Pending;
                trackedAuthor.ExclusionReason = null;
                await _db.SaveChangesAsync(ct);
                await RemoveFromBlacklistIfPresentAsync(trackedAuthor.Name, blacklistSet, ct);
            }

            if (!string.IsNullOrEmpty(entry.OpenLibraryKey))
            {
                // Guard against OL keys that were planted by the old work-count
                // fallback in AuthorRefresher for a completely wrong person.
                // If the folder name and the stored OL display name share no
                // meaningful word token (length > 1, contains a letter), the
                // key almost certainly belongs to someone else — reset it so
                // the next sync re-evaluates with exact-match-only semantics.
                var folderTokens = TitleNormalizer.NormalizeAuthor(entry.FolderName)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var nameTokens = TitleNormalizer.NormalizeAuthor(entry.DisplayName)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .ToHashSet(StringComparer.Ordinal);
                static bool Meaningful(string t) => t.Length > 1 && t.Any(char.IsLetter);
                if (folderTokens.Where(Meaningful).Any(t => nameTokens.Contains(t)))
                    return entry;

                // Mismatch — wipe the suspect key and restore the original name
                // so AuthorRefresher searches with the correct string next sync.
                if (trackedAuthor is not null)
                {
                    trackedAuthor.OpenLibraryKey = null;
                    trackedAuthor.Name = entry.FolderName;
                    trackedAuthor.Status = AuthorStatus.Pending;
                    trackedAuthor.NextFetchAt = null;
                    await _db.SaveChangesAsync(ct);
                }
                // Fall through to backfill; garbage names won't be in the
                // OL catalog → returns null → file routed to __unknown.
            }

            var author = trackedAuthor;
            if (author is null) return null;
            if (!string.IsNullOrEmpty(author.OpenLibraryKey))
            {
                return entry with { OpenLibraryKey = author.OpenLibraryKey };
            }

            // Backfill OL key opportunistically so subsequent runs (and the
            // AuthorRefresher) can skip the lookup.
            foreach (var probe in AuthorMatcher.AuthorKeyVariants(author.Name)
                .Concat(AuthorMatcher.AuthorKeyVariants(author.CalibreFolderName)))
            {
                var hit = await _db.OpenLibraryAuthors
                    .AsNoTracking()
                    .Where(a => a.NormalizedName == probe)
                    .OrderBy(a => a.Id)
                    .Select(a => new { a.OlKey })
                    .FirstOrDefaultAsync(ct);
                if (hit is not null)
                {
                    author.OpenLibraryKey = hit.OlKey;
                    await _db.SaveChangesAsync(ct);
                    return entry with { OpenLibraryKey = hit.OlKey };
                }
            }

            // Tracked author with no OL key yet — still route to their folder.
            // The user explicitly added this author to the watchlist; refusing to
            // deliver files because OL hasn't been seeded / matched yet makes the
            // whole watchlist feature unreliable. OL key will be filled in by
            // AuthorRefresher on the next sync pass.
            _log.LogDebug(
                "Author '{Name}' is tracked but has no OL key yet — routing to folder anyway",
                author.Name);
            return entry with { OpenLibraryKey = null };
        }

        // Keyless entry: only the corroborated-name fallback produces these
        // (embedded metadata + filename agreeing on an author the OL dump
        // doesn't know). During reprocess-unknown, reuse an existing author of
        // that exact name or create a Pending one without an OL key — the
        // refresher backfills the key if OL ever lists them. Outside reprocess
        // a missing key is a safety net and the match is refused.
        if (string.IsNullOrEmpty(entry.OpenLibraryKey))
        {
            if (!autoAddOlAuthors) return null;

            var normName = TitleNormalizer.NormalizeAuthor(entry.DisplayName);
            if (blacklistSet.Contains(normName))
            {
                // During reprocess-unknown, a corroborated author in the blacklist
                // was placed there when the user previously deleted them. The file
                // content now identifies them again — remove the blacklist entry
                // so they can be re-created as Pending below.
                if (!autoAddOlAuthors) return null;
                _log.LogInformation(
                    "Auto-removing '{Name}' from blacklist — identified from file content during unknown reprocess",
                    entry.DisplayName);
                await RemoveFromBlacklistIfPresentAsync(entry.DisplayName, blacklistSet, ct);
            }

            var byName = await _db.Authors.FirstOrDefaultAsync(a => a.Name == entry.DisplayName, ct);
            if (byName is not null)
            {
                if (byName.Status == AuthorStatus.Excluded)
                {
                    if (!autoAddOlAuthors) return null;
                    _log.LogInformation(
                        "Auto-lifting exclusion on author '{Name}' — identified from file content during unknown reprocess",
                        byName.Name);
                    byName.Status = AuthorStatus.Pending;
                    byName.ExclusionReason = null;
                    await _db.SaveChangesAsync(ct);
                }
                return new AuthorIndexEntry(
                    DisplayName: byName.Name,
                    FolderName: !string.IsNullOrWhiteSpace(byName.CalibreFolderName) ? byName.CalibreFolderName! : byName.Name,
                    IsTracked: true,
                    TrackedAuthorId: byName.Id,
                    OpenLibraryKey: byName.OpenLibraryKey);
            }

            var corroboratedAuthor = new Data.Models.Author
            {
                Name = entry.DisplayName,
                CalibreFolderName = entry.FolderName,
                Status = Data.Models.AuthorStatus.Pending,
            };
            _db.Authors.Add(corroboratedAuthor);
            await _db.SaveChangesAsync(ct);
            _log.LogInformation(
                "Auto-created Pending author '{Name}' (metadata+filename corroborated, not on OL) during unknown reprocess",
                corroboratedAuthor.Name);
            return entry with { IsTracked = true, TrackedAuthorId = corroboratedAuthor.Id };
        }

        var existing = await _db.Authors
            .FirstOrDefaultAsync(a => a.OpenLibraryKey == entry.OpenLibraryKey, ct);
        if (existing is not null)
        {
            if (existing.Status == AuthorStatus.Excluded)
            {
                if (!autoAddOlAuthors)
                {
                    _log.LogInformation(
                        "Author '{Name}' ({OlKey}) is EXCLUDED — leaving file in __unknown (not auto-added)",
                        existing.Name, entry.OpenLibraryKey);
                    return null;
                }
                _log.LogInformation(
                    "Auto-lifting exclusion on author '{Name}' ({OlKey}) — identified from file content during unknown reprocess",
                    existing.Name, entry.OpenLibraryKey);
                existing.Status = AuthorStatus.Pending;
                existing.ExclusionReason = null;
                await _db.SaveChangesAsync(ct);
                await RemoveFromBlacklistIfPresentAsync(existing.Name, blacklistSet, ct);
            }

            var calibreFolder = existing.CalibreFolderName;
            var folderName = !string.IsNullOrWhiteSpace(calibreFolder) && TitleNormalizer.IsPlausibleAuthorName(calibreFolder)
                ? calibreFolder
                : existing.Name;
            return new AuthorIndexEntry(
                DisplayName: existing.Name,
                FolderName: folderName,
                IsTracked: true,
                TrackedAuthorId: existing.Id,
                OpenLibraryKey: existing.OpenLibraryKey);
        }

        var normDisplay = TitleNormalizer.NormalizeAuthor(entry.DisplayName);
        var normFolder = TitleNormalizer.NormalizeAuthor(entry.FolderName);
        if (blacklistSet.Contains(normDisplay) || blacklistSet.Contains(normFolder))
        {
            if (!autoAddOlAuthors)
            {
                _log.LogInformation(
                    "Author '{Name}' ({OlKey}) is BLACKLISTED — leaving file in __unknown (not auto-added)",
                    entry.DisplayName, entry.OpenLibraryKey);
                return null;
            }
            _log.LogInformation(
                "Auto-removing '{Name}' ({OlKey}) from blacklist — identified from file content during unknown reprocess",
                entry.DisplayName, entry.OpenLibraryKey);
            await RemoveFromBlacklistIfPresentAsync(entry.DisplayName, blacklistSet, ct);
            await RemoveFromBlacklistIfPresentAsync(entry.FolderName, blacklistSet, ct);
        }

        // Author matched OL but is not on the watchlist yet.
        // During a reprocess-unknown run we auto-create a Pending Author row so
        // the file can be delivered immediately instead of staying in quarantine
        // indefinitely waiting for the user to manually add the author.
        // During a regular incoming run (autoAddOlAuthors=false) we keep the
        // original "user must add them first" policy unchanged.
        if (autoAddOlAuthors)
        {
            var newAuthor = new Data.Models.Author
            {
                Name              = entry.DisplayName,
                CalibreFolderName = entry.FolderName,
                OpenLibraryKey    = entry.OpenLibraryKey,
                Status            = Data.Models.AuthorStatus.Pending,
            };
            _db.Authors.Add(newAuthor);
            await _db.SaveChangesAsync(ct);
            _log.LogInformation(
                "Auto-created Pending author '{Name}' ({OlKey}) during unknown reprocess",
                newAuthor.Name, newAuthor.OpenLibraryKey);
            return entry with { IsTracked = true, TrackedAuthorId = newAuthor.Id };
        }

        // Author matched OL but is not on the watchlist — route to __unknown.
        // User must explicitly add the author before files land in the main collection.
        _log.LogDebug(
            "Author '{Name}' ({OlKey}) matched OL but is not tracked — routing to __unknown",
            entry.DisplayName, entry.OpenLibraryKey);
        return null;
    }

    // Removes all blacklist entries whose NormalizedName matches the given name
    // and also purges the name from the in-memory set so the current run sees
    // the change immediately. Safe to call when no entry exists.
    private async Task RemoveFromBlacklistIfPresentAsync(
        string name, HashSet<string> blacklistSet, CancellationToken ct)
    {
        var norm = TitleNormalizer.NormalizeAuthor(name);
        var rows = await _db.AuthorBlacklist
            .Where(b => b.NormalizedName == norm)
            .ToListAsync(ct);
        if (rows.Count > 0)
        {
            _db.AuthorBlacklist.RemoveRange(rows);
            await _db.SaveChangesAsync(ct);
        }
        blacklistSet.Remove(norm);
    }

    private static readonly System.Text.RegularExpressions.Regex CorroborationAuthorSeparator = new(
        @"\s*(?:;|&|/|\s+(?:and|AND|And|with)\s+)\s*",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    // The corroborated-name fallback for reprocess-unknown: the file's own
    // EMBEDDED metadata author (never the filename-derived fallback — that
    // would be circular) must agree, via the matcher's variant expansion, with
    // an author candidate parsed from the FILENAME. Two independent sources
    // naming the same plausible person is the acceptance bar that replaces the
    // OL catalogue for authors the dump doesn't know. Returns the display name
    // and the agreeing interpretation's title.
    internal static (string Author, string? Title)? FindCorroboratedAuthor(
        EpubMetadata? embedded, string file, HashSet<string> blacklist)
    {
        var raw = embedded?.Author;
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // First author of a joint credit; trim credit junk.
        var name = CorroborationAuthorSeparator.Split(raw)[0].Trim().Trim(',', '.', '-').Trim();
        if (!LooksLikePersonalName(name)) return null;

        var keys = AuthorMatcher.AuthorKeyVariants(name).ToHashSet(StringComparer.Ordinal);
        if (keys.Count == 0 || keys.Any(blacklist.Contains)) return null;

        foreach (var g in FilenameGuesser.Interpret(file))
        {
            if (string.IsNullOrWhiteSpace(g.Author)) continue;
            if (AuthorMatcher.AuthorKeyVariants(g.Author).Any(keys.Contains))
                return (name, g.Title ?? embedded!.Title);
        }
        return null;
    }

    // Stricter than IsPlausibleAuthorName — this gate admits a name WITHOUT
    // catalogue backing, so it must look like a real person: 2–4 tokens, no
    // digits, sane length, and each token either capitalised, an initial, or
    // a lowercase name particle (van/von/de/…).
    private static readonly HashSet<string> NameParticles = new(StringComparer.Ordinal)
    { "van", "von", "de", "der", "den", "del", "della", "di", "da", "du", "la", "le", "el", "al", "bin", "ibn", "mac", "mc", "st" };

    internal static bool LooksLikePersonalName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var s = name.Trim();
        if (s.Length is < 5 or > 40) return false;
        if (s.Any(char.IsDigit)) return false;
        var tokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is < 2 or > 4) return false;
        foreach (var t in tokens)
        {
            if (NameParticles.Contains(t)) continue;
            if (!char.IsLetter(t[0]) || !char.IsUpper(t[0])) return false;
        }
        return true;
    }

    private EpubMetadata? ReadMetadata(string file)
    {
        var m = FileMetadataReader.TryRead(file, _log);
        if (m is not null && (m.Title is not null || m.Author is not null)) return m;

        // Fallback: infer from the filename.
        //
        // Pattern 1 — series convention: "{Series} [#]N - {Title} - {Author, Last}"
        //   e.g. "Chaoswar Saga 03 - Magician's End - Feist, Raymond E_.epub"
        //
        // Pattern 2 — libgen / calibre convention: "{Author} - {Title}.ext"
        var stem = Path.GetFileNameWithoutExtension(file);
        var (series, seriesPos, parsedTitle, parsedAuthor) = TitleNormalizer.TryParseSeriesFilename(stem);
        if (parsedTitle is not null)
        {
            // Author from this pattern is in "Last, First" sort order — use as
            // both Author and AuthorSort so the matcher can normalise it.
            return new EpubMetadata(parsedTitle, parsedAuthor, parsedAuthor, m?.Language, null,
                series, seriesPos);
        }

        // Pattern 2: "Author - Title" (author first).
        var dash = stem.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
        {
            var author = stem[..dash].Trim();
            var title = stem[(dash + 3)..].Trim();
            return new EpubMetadata(title, author, null, m?.Language, null);
        }
        return new EpubMetadata(stem, null, null, m?.Language, null);
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
            try { files = _fs.EnumerateFiles(dir, "*", opts).ToList(); }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Skipping unreadable files in {Dir}", dir);
                files = new List<string>();
            }

            try
            {
                foreach (var sub in _fs.EnumerateDirectories(dir, "*", opts))
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
    private void DeleteAndWait(string path)
    {
        const int maxAttempts = 6;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (_fs.FileExists(path)) _fs.DeleteFile(path);
                for (var i = 0; i < 20 && _fs.FileExists(path); i++) Thread.Sleep(50);
                if (!_fs.FileExists(path)) return;
            }
            catch (IOException) when (attempt < maxAttempts) { }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts) { }
            Thread.Sleep(200 * attempt);
        }
        // Final attempt without swallowing — let the caller log the failure.
        _fs.DeleteFile(path);
    }

    private string UniqueDestPath(string preferred)
    {
        var dir = Path.GetDirectoryName(preferred) ?? "";
        var stem = Path.GetFileNameWithoutExtension(preferred);
        var ext = Path.GetExtension(preferred);
        for (int n = 1; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem}_{n}{ext}");
            if (!_fs.FileExists(candidate)) return candidate;
        }
    }

    private void MoveAndWait(string src, string dst, bool overwrite)
    {
        const int maxAttempts = 6;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                // .NET's File.Move(overwrite: true) only honors `overwrite`
                // when source and destination are on the SAME volume (a plain
                // rename). Across volumes — which is the norm in Docker/Azure
                // where the incoming and __unknown folders are separate mounts —
                // it falls back to copy+delete via LinkOrCopyFile, and that
                // path throws "already exists" if the target is present. Clear
                // the destination ourselves first so the cross-volume copy has
                // a clean target.
                if (overwrite && _fs.FileExists(dst))
                {
                    try { _fs.DeleteFile(dst); }
                    catch (IOException) when (attempt < maxAttempts) { }
                    catch (UnauthorizedAccessException) when (attempt < maxAttempts) { }
                }
                _fs.MoveFile(src, dst, overwrite);
                for (var i = 0; i < 20 && (_fs.FileExists(src) || !_fs.FileExists(dst)); i++)
                    Thread.Sleep(50);
                if (!_fs.FileExists(src) && _fs.FileExists(dst)) return;
            }
            catch (IOException) when (attempt < maxAttempts) { }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts) { }
            Thread.Sleep(200 * attempt);
        }
        if (overwrite && _fs.FileExists(dst))
        {
            try { _fs.DeleteFile(dst); } catch { /* let the move surface the real error */ }
        }
        _fs.MoveFile(src, dst, overwrite);
    }

    // Remove empty directories bottom-up so a drained incoming folder doesn't
    // leave clutter behind. Recurses depth-first and only evaluates a folder
    // after its children have been processed, so chains of nested-empty
    // folders collapse in one pass. Doesn't touch the root itself.
    private void CleanupEmptyDirs(string root, Action<string> emit)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        CleanDir(root, normalizedRoot, emit);
    }

    private void CleanDir(string dir, string root, Action<string> emit)
    {
        List<string> subs;
        try { subs = _fs.EnumerateDirectories(dir).ToList(); }
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
            if (!_fs.EnumerateFileSystemEntries(dir).Any())
            {
                _fs.DeleteDirectory(dir);
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

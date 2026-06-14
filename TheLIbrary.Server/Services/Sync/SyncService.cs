using System.Data;
using Hangfire;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.AuthorUpdates;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.Incoming;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

// Sync pipeline:
//   1. Scan every enabled library location (Calibre layout).
//   2. Ensure an Author row exists for each Calibre author folder.
//   3. For every Author (whether Calibre-derived or manually added), resolve
//      its OpenLibrary key if missing, then fetch English works.
//   4. Link each Calibre folder/file to its author and book by normalized name.
//   5. Prune LocalBookFile rows not seen during this run.
public sealed class SyncService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SyncService> _log;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly object _stateLock = new();
    private SyncState _state = new();

    public SyncService(IServiceScopeFactory scopeFactory, BackgroundTaskCoordinator coordinator, ILogger<SyncService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _log = log;
    }

    public SyncState GetState()
    {
        lock (_stateLock) return Clone(_state);
    }

    public bool IsRunning
    {
        get { lock (_stateLock) return _state.Phase is not SyncPhase.Idle and not SyncPhase.Done and not SyncPhase.Failed; }
    }

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("sync", out var holder))
        {
            error = $"Another task is already running ({holder})";
            return false;
        }
        error = null;

        lock (_stateLock) _state = new SyncState { Phase = SyncPhase.ScanningCalibre, StartedAt = DateTime.UtcNow };

        _ = Task.Run(async () =>
        {
            try { await RunInternalAsync(hostCt); }
            catch (OperationCanceledException) when (hostCt.IsCancellationRequested)
            {
                _log.LogWarning("Sync canceled");
                MutateState(s => { s.Phase = SyncPhase.Failed; s.Error = "Canceled"; s.FinishedAt = DateTime.UtcNow; });
            }
            catch (Exception ex)
            {
                var step = GetState().Message;
                _log.LogError(ex, "Sync failed during step: {Step}", step);
                var flat = ExceptionFormatter.Flatten(ex);
                var full = string.IsNullOrWhiteSpace(step) ? flat : $"During \"{step}\": {flat}";
                MutateState(s => { s.Phase = SyncPhase.Failed; s.Error = full; s.FinishedAt = DateTime.UtcNow; });
            }
            finally { _coordinator.Release(); }
        }, hostCt);

        return true;
    }

    private async Task RunInternalAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var scanner = scope.ServiceProvider.GetRequiredService<CalibreScanner>();

        var syncStartedUtc = DateTime.UtcNow;

        // Phase 1: scan library locations.
        var locations = await db.LibraryLocations.Where(l => l.Enabled).ToListAsync(ct);
        var roots = locations.Select(l => l.Path).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        MutateState(s =>
        {
            s.Phase = SyncPhase.ScanningCalibre;
            s.Message = roots.Count == 0
                ? "No library locations configured"
                : $"Scanning {roots.Count} location(s)";
        });
        var entries = scanner.Scan(roots);
        MutateState(s => s.LocalFilesSeen = entries.Count);

        foreach (var loc in locations) loc.LastScanAt = syncStartedUtc;
        await db.SaveChangesAsync(ct);

        // Phase 2: reconcile every Calibre folder to a DB Author row (opportunistically
        // stamping an OL key from the local catalog so Phase 3 can skip the OL
        // search step entirely for any folder whose name normalizes to a known
        // OL author).
        MutateState(s => { s.Phase = SyncPhase.ResolvingAuthors; s.Message = "Registering authors from Calibre folders"; });
        await ReconcileAuthorFoldersAsync(db, entries, ct);

        // Phase 3: per-author unified pass — resolve OL key and fetch works
        // when the author is past its NextFetchAt, then match THIS author's
        // local files to THEIR books. Keeping the full per-author lifecycle
        // together means a canceled run leaves authors in consistent states
        // and every OL round-trip we make sits next to the work that needs
        // its result, reducing redundant lookups under OL's rate limits.
        await ProcessAuthorsAsync(db, entries, ct);

        // Phase 4: index the __unknown quarantine folder into the DB so the
        // missing-works "find matching files" search can score those files
        // without walking the disk on every request.
        MutateState(s => s.Message = "Indexing unknown folder");
        var unkResult = await UnknownFileIndexer.RescanAsync(db, roots, ct);
        _log.LogInformation(
            "Unknown folder index: {Seen} ebook file(s) across {Roots} root(s) ({Missing} missing); +{Added} / -{Removed}; total {Total}",
            unkResult.EbookFilesSeen, unkResult.RootsChecked.Count, unkResult.RootsMissing.Count,
            unkResult.Added, unkResult.Removed, unkResult.Total);

        MutateState(s =>
        {
            s.Phase = SyncPhase.Done;
            s.FinishedAt = DateTime.UtcNow;
            s.Message = "Complete";
        });
    }

    // Reconciles Calibre folders against the tracked-author watchlist.
    // Only folders whose Author row has Priority>0 or Status=Active stay in the
    // main collection — everything else is relocated to __unknown. New folders
    // with no existing Author row are also sent to __unknown; authors must be
    // added explicitly via the "Add author" dialog, not auto-discovered from disk.
    private async Task ReconcileAuthorFoldersAsync(
        LibraryDbContext db, IReadOnlyList<CalibreBookEntry> entries, CancellationToken ct)
    {
        var folderGroups = entries
            .GroupBy(e => e.AuthorFolder, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var blacklistedNormalized = (await db.AuthorBlacklist
            .AsNoTracking()
            .Select(b => b.NormalizedName)
            .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var dbAuthors = await db.Authors.ToListAsync(ct);

        var authorByKey = new Dictionary<string, Author>(StringComparer.Ordinal);
        foreach (var a in dbAuthors)
            foreach (var k in AuthorKeys(a)) authorByKey.TryAdd(k, a);

        var movedFolders = new List<string>();
        var blacklistKeysToClear = new List<string>();
        var unknownRootCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        async Task<string> UnknownRootFor(string locationPath)
        {
            if (!unknownRootCache.TryGetValue(locationPath, out var cached))
            {
                cached = await Services.Calibre.UnknownFolderResolver.GetDestinationRootAsync(db, locationPath, ct);
                unknownRootCache[locationPath] = cached;
            }
            return cached;
        }

        foreach (var group in folderGroups)
        {
            ct.ThrowIfCancellationRequested();
            var folder = group.Key;
            var folderKey = TitleNormalizer.NormalizeAuthor(folder);
            var locationPath = group.First().LocationPath;

            var hasAuthor = authorByKey.TryGetValue(folderKey, out var existing);
            var isBlacklisted = blacklistedNormalized.Contains(folderKey);

            // This folder is on disk, so we physically hold files for this author.
            // Rule: an author whose files we hold is never excluded or blacklisted.
            // The refresher's "no recent OpenLibrary works" exclusion (and stale
            // manual ones) used to mark such authors Excluded, after which this
            // reconciliation swept their whole folder into __unknown every sync —
            // steadily draining the real library into the quarantine. Heal both
            // states here and KEEP the folder instead of quarantining it.
            if (hasAuthor || isBlacklisted)
            {
                if (isBlacklisted && blacklistedNormalized.Remove(folderKey))
                    blacklistKeysToClear.Add(folderKey);

                if (existing is not null)
                {
                    if (existing.Status == AuthorStatus.Excluded)
                    {
                        existing.Status = AuthorStatus.Active;
                        existing.ExclusionReason = null;
                        _log.LogInformation(
                            "Un-excluded '{Name}' — their files are present on disk", existing.Name);
                    }
                    if (string.IsNullOrEmpty(existing.CalibreFolderName))
                        existing.CalibreFolderName = folder;
                }
                continue;
            }

            // No Author row and not blacklisted — move to __unknown. Authors must
            // be added explicitly, not auto-discovered from disk.
            var unknownRoot = await UnknownRootFor(locationPath);
            if (MoveToUnknown(locationPath, folder, unknownRoot)) movedFolders.Add(folder);
        }

        // Drop blacklist entries for any author whose files turned up on disk.
        if (blacklistKeysToClear.Count > 0)
        {
            var removed = await db.AuthorBlacklist
                .Where(x => blacklistKeysToClear.Contains(x.NormalizedName))
                .ExecuteDeleteAsync(ct);
            if (removed > 0)
                _log.LogInformation(
                    "Removed {N} blacklist entr(ies) for author(s) whose files are present on disk", removed);
        }

        // Purge LocalBookFile rows for every relocated folder. Those paths are no
        // longer scannable (CalibreScanner ignores __unknown), so the rows are
        // immediately stale and would linger as garbage in the unclaimed view.
        if (movedFolders.Count > 0)
        {
            var affected = await db.LocalBookFiles
                .Where(f => movedFolders.Contains(f.AuthorFolder))
                .ExecuteDeleteAsync(ct);
            if (affected > 0)
                _log.LogInformation(
                    "Removed {Lbf} LocalBookFile row(s) for {N} folder(s) relocated to __unknown",
                    affected, movedFolders.Count);
        }

        await db.SaveChangesAsync(ct);

        await FlattenQuarantineRootsAsync(db, ct);
    }

    // Self-heal invariant: the __unknown quarantine is a FLAT bucket of loose
    // files — never author/title subfolders. Whatever creates a folder under a
    // quarantine root (an older build, a misrouted assign/move, a manual drop),
    // this pass — run at the end of every sync — flattens its files up to the
    // root and removes the folder. Combined with every writer routing through
    // UnknownQuarantine, it makes "no folders in __unknown" a continuously
    // enforced invariant rather than something a single code path can break.
    private async Task FlattenQuarantineRootsAsync(LibraryDbContext db, CancellationToken ct)
    {
        var locPaths = await db.LibraryLocations.AsNoTracking()
            .Where(l => l.Enabled).Select(l => l.Path).ToListAsync(ct);
        var quarantineRoots = await Services.Calibre.UnknownFolderResolver.GetSourceRootsAsync(db, locPaths, ct);

        var flattenedFiles = 0;
        var flattenedFolders = 0;
        foreach (var qroot in quarantineRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(qroot)) continue;
            foreach (var sub in Directory.GetDirectories(qroot))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var moved = UnknownQuarantine.FlattenFolderIntoRoot(qroot, sub);
                    flattenedFiles += moved;
                    flattenedFolders++;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Sync: could not flatten quarantine subfolder {Dir}", sub);
                }
            }
        }
        if (flattenedFolders > 0)
            _log.LogInformation(
                "Sync: flattened {Files} file(s) out of {Folders} quarantine subfolder(s) into the __unknown root",
                flattenedFiles, flattenedFolders);
    }

    private bool MoveToUnknown(string locationPath, string folderName, string unknownRoot)
    {
        var src = Path.Combine(locationPath, folderName);
        if (!Directory.Exists(src)) return true;

        try
        {
            // FLATTEN into the quarantine root — never recreate the author/title
            // folder tree under __unknown. Moving the whole folder (the old
            // behaviour) is exactly what kept repopulating the quarantine with
            // subfolders; the reprocess job reads each file's name/metadata, so
            // the folder grouping is worthless there.
            var moved = UnknownQuarantine.FlattenFolderIntoRoot(unknownRoot, src);
            _log.LogDebug("Flattened '{Folder}' → __unknown root ({Count} file(s))", folderName, moved);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not move '{Folder}' to __unknown — will retry next sync", folderName);
            return false;
        }
    }

    // Per-author file-matching pass. No OL HTTP calls — those belong in the
    // dedicated "Refresh Works" job. This pass only links what's already in
    // the DB: three bulk queries (authors, books, existing LBF rows) then
    // pure in-memory matching, followed by a single batch save.
    private async Task ProcessAuthorsAsync(
        LibraryDbContext db,
        IReadOnlyList<CalibreBookEntry> entries,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var authors = await db.Authors
            .OrderBy(a => a.CalibreScannedAt.HasValue)
            .ThenBy(a => a.CalibreScannedAt)
            .ToListAsync(ct);

        MutateState(s =>
        {
            s.Phase = SyncPhase.FetchingWorks;
            s.AuthorsTotal = authors.Count;
            s.AuthorsProcessed = 0;
            s.Message = $"Matching files for {authors.Count} author(s)";
        });

        static string Canon(string p) =>
            p.Normalize(System.Text.NormalizationForm.FormC).ToUpperInvariant();

        var deduped = new Dictionary<string, CalibreBookEntry>(StringComparer.Ordinal);
        foreach (var e in entries) deduped[Canon(e.FullPath)] = e;

        var entriesByFolderKey = deduped.Values
            .GroupBy(e => TitleNormalizer.NormalizeAuthor(e.AuthorFolder), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // AsNoTracking: we do not want EF scanning 50k+ rows for changes on
        // every SaveChangesAsync. Only explicitly-modified rows are attached below.
        var existingList = await db.LocalBookFiles.AsNoTracking().ToListAsync(ct);
        var existingByPath = new Dictionary<string, LocalBookFile>(StringComparer.Ordinal);
        foreach (var f in existingList) existingByPath[Canon(f.FullPath)] = f;

        // Migration bridge: for records whose FullPath points to a folder (classic
        // Calibre layout), also index by the primary ebook file inside that folder.
        // This lets the updated scanner (which returns file-path entries for the
        // flat-file layout) find and migrate old folder-path records transparently
        // on the first sync after the series organizer has moved the files.
        foreach (var lbf in existingList)
        {
            if (!Directory.Exists(lbf.FullPath)) continue;
            var primary = PrimaryEbookInFolder(lbf.FullPath);
            if (primary is not null)
                existingByPath.TryAdd(Canon(primary), lbf);
        }

        // Identify which author-folder keys have any file change (new, modified,
        // or removed). Authors with no changes are skipped entirely — no DB write,
        // no matching pass. This is the primary driver of the whole sync.
        var changedFolderKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in deduped.Values)
        {
            var canon = Canon(entry.FullPath);
            if (!existingByPath.TryGetValue(canon, out var ex) ||
                ex.SizeBytes != entry.SizeBytes ||
                ex.ModifiedAt != entry.ModifiedAt)
            {
                changedFolderKeys.Add(TitleNormalizer.NormalizeAuthor(entry.AuthorFolder));
            }
        }
        // Removed files also count as changes for the owning author.
        foreach (var ex in existingList)
        {
            if (!deduped.ContainsKey(Canon(ex.FullPath)))
                changedFolderKeys.Add(TitleNormalizer.NormalizeAuthor(ex.AuthorFolder));
        }
        // Files with a missing AuthorId in a tracked folder need re-matching even
        // when their on-disk size/date are unchanged (previously wiped by orphan pass).
        foreach (var ex in existingList)
        {
            if (ex.AuthorId == null)
                changedFolderKeys.Add(TitleNormalizer.NormalizeAuthor(ex.AuthorFolder));
        }

        // Load ALL books in one query — eliminates the per-author N+1 round trip.
        var booksByAuthorId = (await db.Books.AsNoTracking().ToListAsync(ct))
            .GroupBy(b => b.AuthorId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Map canonical → list of non-pen-name child author ids. Used by
        // MatchAuthorFiles so a file at the canonical's folder can match a book
        // owned by one of the merged-in child authors.
        var nonPenNameChildrenByCanonical = authors
            .Where(a => a.LinkedToAuthorId is not null && !a.IsPenName)
            .GroupBy(a => a.LinkedToAuthorId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(a => a.Id).ToList());

        var processed = new HashSet<string>(StringComparer.Ordinal);
        var processedAuthorIds = new List<int>();
        int skipped = 0;
        var toInsert = new List<LocalBookFile>();
        var toUpdate = new List<LocalBookFile>();

        foreach (var author in authors)
        {
            ct.ThrowIfCancellationRequested();

            // Skip authors whose file set is entirely unchanged.
            var hasChange = AuthorKeys(author).Any(k => changedFolderKeys.Contains(k));
            if (!hasChange)
            {
                // Mark this author's files as processed so the orphan pass below
                // does not overwrite their AuthorId with null.
                foreach (var k in AuthorKeys(author))
                    if (entriesByFolderKey.TryGetValue(k, out var skipList))
                        foreach (var e in skipList)
                            processed.Add(Canon(e.FullPath));
                skipped++;
                MutateState(s => s.AuthorsProcessed++);
                continue;
            }

            MatchAuthorFiles(author, entriesByFolderKey, booksByAuthorId, nonPenNameChildrenByCanonical, existingByPath, processed, toInsert, toUpdate);
            processedAuthorIds.Add(author.Id);
            MutateState(s => s.AuthorsProcessed++);
        }

        if (skipped > 0)
            _log.LogInformation("Skipped {Count} author(s) with no file changes", skipped);

        // Orphan pass: only surface files from OL-verified (tracked) author folders
        // as unclaimed. Files in folders with no matching Author row have no OL
        // pedigree — recording them just floods the unclaimed view with garbage
        // metadata entries from ebook files. Truly unresolvable files stay on disk
        // but are invisible to the DB until the user explicitly adds the author.
        MutateState(s => s.Message = "Recording orphan entries");

        var trackedFolderKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in authors.Where(a => a.Status != AuthorStatus.Excluded))
            foreach (var k in AuthorKeys(a))
                trackedFolderKeys.Add(k);

        foreach (var (canon, entry) in deduped)
        {
            if (processed.Contains(canon)) continue;
            if (!trackedFolderKeys.Contains(TitleNormalizer.NormalizeAuthor(entry.AuthorFolder))) continue;
            UpsertLocalFile(entry, authorId: null, bookId: null, existingByPath, canon, toInsert, toUpdate);
        }

        MutateState(s => s.Message = "Saving");
        await BulkUpsertLocalFilesAsync(toInsert, toUpdate, db, ct);

        // Stamp CalibreScannedAt for all processed authors in one SQL statement.
        if (processedAuthorIds.Count > 0)
        {
            await db.Authors
                .Where(a => processedAuthorIds.Contains(a.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.CalibreScannedAt, _ => now), ct);
        }

        // Delete rows for paths no longer on disk (set-based; no timestamp needed).
        // Guard: if the scan found nothing at all, something went wrong (library
        // location offline, misconfigured path, etc.) — never wipe the catalogue.
        // Guard: if more than 1000 rows would be removed, something is almost
        // certainly wrong (drive remapped, UNC path changed, etc.); refuse and log.
        // Exclude records that were updated this run — when a folder-path record was
        // migrated to a file path, its old folder-path key is absent from deduped but
        // the record is live under its new file-path key. Deleting it would undo the
        // migration. Also de-duplicate since secondary keys can produce the same Id twice.
        var updatedIds = new HashSet<int>(toUpdate.Select(f => f.Id));
        var removedIds = existingByPath
            .Where(kvp => !deduped.ContainsKey(kvp.Key) && !updatedIds.Contains(kvp.Value.Id))
            .Select(kvp => kvp.Value.Id)
            .Distinct()
            .ToList();
        if (removedIds.Count == 0)
        {
            // Nothing to remove.
        }
        else if (entries.Count == 0)
        {
            _log.LogWarning(
                "Scan found 0 files but {Count} LocalBookFile row(s) exist — skipping deletion to prevent data loss. " +
                "Check that library locations are configured and accessible.",
                removedIds.Count);
        }
        else if (removedIds.Count > 1000)
        {
            _log.LogWarning(
                "Scan would remove {Count} LocalBookFile row(s) (>{Threshold}). " +
                "This likely indicates a path change or inaccessible drive. Deletion skipped.",
                removedIds.Count, 1000);
        }
        else
        {
            _log.LogInformation("Removing {Count} LocalBookFile row(s) no longer on disk", removedIds.Count);
            await db.LocalBookFiles.Where(f => removedIds.Contains(f.Id)).ExecuteDeleteAsync(ct);
            MutateState(s => s.LocalFilesSeen -= removedIds.Count);
        }

        // One-time cleanup: delete any orphan (AuthorId=null) LocalBookFile rows
        // whose folder has no tracked Author. These were created by earlier sync
        // runs before the orphan-pass filter was added and would otherwise linger
        // as garbage in the unclaimed view indefinitely.
        var staleOrphanIds = existingList
            .Where(f => f.AuthorId == null &&
                        !trackedFolderKeys.Contains(TitleNormalizer.NormalizeAuthor(f.AuthorFolder)))
            .Select(f => f.Id)
            .ToList();
        if (staleOrphanIds.Count > 0)
        {
            _log.LogInformation(
                "Removing {Count} stale orphan LocalBookFile row(s) from untracked folders",
                staleOrphanIds.Count);
            await db.LocalBookFiles.Where(f => staleOrphanIds.Contains(f.Id)).ExecuteDeleteAsync(ct);
            MutateState(s => s.LocalFilesSeen -= staleOrphanIds.Count);
        }

        // Guard-bypassing cleanup for records that are definitively stale but were
        // shielded by the >1000 guard above:
        //
        //   • Directory-path records not found by the scanner — the scanner emits a
        //     directory entry only when the folder has no files; if it emitted nothing
        //     for this path the folder is either gone or was reorganised. Safe to delete
        //     unconditionally: no drive-offline scenario produces phantom directory entries.
        //
        //   • File-path records not found by the scanner where File.Exists returns false —
        //     the file is definitively gone from disk, not merely on an offline drive.
        if (entries.Count > 0)
        {
            var definitelyStaleIds = existingList
                .Where(f => !deduped.ContainsKey(Canon(f.FullPath)))
                .Where(f => string.IsNullOrEmpty(Path.GetExtension(f.FullPath))
                             ? true                     // directory-path: always stale if not in deduped
                             : !File.Exists(f.FullPath)) // file-path: only if file is gone from disk
                .Select(f => f.Id)
                .ToList();
            if (definitelyStaleIds.Count > 0)
            {
                _log.LogInformation(
                    "Removing {Count} definitively-stale LocalBookFile record(s) (bypassing bulk-delete guard)",
                    definitelyStaleIds.Count);
                await db.LocalBookFiles
                    .Where(f => definitelyStaleIds.Contains(f.Id))
                    .ExecuteDeleteAsync(ct);
                MutateState(s => s.LocalFilesSeen -= definitelyStaleIds.Count);
            }
        }
    }

    // Returns the primary ebook file inside a folder (epub > pdf > other).
    // Used to build a secondary existingByPath key so file-path scanner entries
    // can find and migrate old folder-path DB records during the transition.
    private static string? PrimaryEbookInFolder(string folder)
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

    private static void MatchAuthorFiles(
        Author author,
        Dictionary<string, List<CalibreBookEntry>> entriesByFolderKey,
        Dictionary<int, List<Book>> booksByAuthorId,
        Dictionary<int, List<int>> nonPenNameChildrenByCanonical,
        Dictionary<string, LocalBookFile> existingByPath,
        HashSet<string> processed,
        List<LocalBookFile> toInsert,
        List<LocalBookFile> toUpdate)
    {
        static string Canon(string p) =>
            p.Normalize(System.Text.NormalizationForm.FormC).ToUpperInvariant();

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in AuthorKeys(author)) keys.Add(k);

        var relevantEntries = new List<CalibreBookEntry>();
        foreach (var key in keys)
            if (entriesByFolderKey.TryGetValue(key, out var list))
                relevantEntries.AddRange(list);

        if (relevantEntries.Count == 0) return;

        // Build the match dict from the author's own books PLUS the books of any
        // non-pen-name child authors folded into this view. When the user links
        // "T. Brooks" → "Terry Brooks" as a duplicate, files now live under
        // Terry's folder but the child's books still carry AuthorId = T's id —
        // we need to include them or every linked book would auto-orphan.
        var bookByTitle = new Dictionary<string, Book>(StringComparer.Ordinal);
        void Add(IEnumerable<Book> bs)
        {
            foreach (var b in bs)
            {
                // A book the user has hidden (suppressed) or flagged foreign is NOT
                // a valid match target — they've already decided they don't want it.
                // The suggestions UI already filters these; the auto-matcher must too,
                // otherwise a suppressed junk book the user can no longer even see in
                // the list keeps getting files auto-linked to it on every sync.
                if (b.Suppressed || b.Foreign) continue;
                var t = b.NormalizedTitle ?? "";
                if (!bookByTitle.ContainsKey(t)) bookByTitle[t] = b;
            }
        }
        if (booksByAuthorId.TryGetValue(author.Id, out var authorBooks))
            Add(authorBooks);
        if (nonPenNameChildrenByCanonical.TryGetValue(author.Id, out var childIds))
            foreach (var childId in childIds)
                if (booksByAuthorId.TryGetValue(childId, out var childBooks))
                    Add(childBooks);

        foreach (var entry in relevantEntries)
        {
            var canon = Canon(entry.FullPath);
            if (processed.Contains(canon)) continue;

            Book? matchedBook = null;

            // Flat-file layout puts the filename stem in TitleFolder. When the
            // stem is "<Author> - <Title>" or "<Title> - <Author>", the raw
            // string normalises to a key that includes the author and won't
            // match any book. Strip a leading/trailing segment that resolves
            // to this author (in any of the watchlist's name variants) and
            // try the remainder first. The raw stem is still tried as a
            // fallback so genuine "by Author" titles aren't lost.
            foreach (var raw in TitleStemCandidates(entry.TitleFolder, author))
            {
                foreach (var candidate in TitleNormalizer.FolderTitleCandidates(raw))
                {
                    if (bookByTitle.TryGetValue(candidate, out matchedBook)) break;
                }
                if (matchedBook is not null) break;
            }

            UpsertLocalFile(entry, author.Id, matchedBook?.Id, existingByPath, canon, toInsert, toUpdate);
            processed.Add(canon);
        }
    }

    // Yields the candidate "title stems" to feed into FolderTitleCandidates
    // for a file, ordered from most-likely-correct to least. When the raw stem
    // looks like "<Author> - <Title>" (or vice versa), the title-only stripped
    // form comes first so files whose actual title is "Magic Kingdom for Sale"
    // don't get matched against "Terry Brooks Magic Kingdom for Sale". Files
    // whose stem matches the series-filename grammar also yield the parsed
    // title separately, so "Heechee 6 - The Boy Who Would Live Forever" tries
    // both the full stem and the bare "The Boy Who Would Live Forever".
    internal static IEnumerable<string> TitleStemCandidates(string stem, Author author)
    {
        if (string.IsNullOrWhiteSpace(stem)) yield break;
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        // 1) Author-prefix/suffix stripped form — highest signal.
        var stripped = StripAuthorPrefixOrSuffix(stem, author);
        if (!string.IsNullOrWhiteSpace(stripped) &&
            !string.Equals(stripped, stem, StringComparison.Ordinal) &&
            emitted.Add(stripped))
        {
            yield return stripped;
        }

        // 2) Series-filename grammar — "Series N - Title [- Author]" exposes
        //    a clean title we wouldn't otherwise extract.
        var (_, _, parsedTitle, _) = TitleNormalizer.TryParseSeriesFilename(stem);
        if (!string.IsNullOrWhiteSpace(parsedTitle) && emitted.Add(parsedTitle!))
            yield return parsedTitle!;
        // Also apply series parse to the already-stripped form, since
        // "<Author> - Series N - Title" needs both passes.
        if (!string.Equals(stripped, stem, StringComparison.Ordinal))
        {
            var (_, _, parsedFromStripped, _) = TitleNormalizer.TryParseSeriesFilename(stripped);
            if (!string.IsNullOrWhiteSpace(parsedFromStripped) && emitted.Add(parsedFromStripped!))
                yield return parsedFromStripped!;
        }

        // 3) Raw stem as the catch-all fallback.
        if (emitted.Add(stem)) yield return stem;
    }

    // Returns `stem` with a leading "<Author> - " or trailing " - <Author>"
    // segment removed when one of the segments normalises to the author name
    // (or one of its expanded variants). Returns the original stem unchanged
    // when no segment matches.
    internal static string StripAuthorPrefixOrSuffix(string stem, Author author)
    {
        const string sep = " - ";
        var firstDash = stem.IndexOf(sep, StringComparison.Ordinal);
        if (firstDash < 0) return stem;

        if (MatchesAuthor(stem[..firstDash], author))
            return stem[(firstDash + sep.Length)..].Trim();

        // Author may instead live at the END of the stem ("<Title> - <Author>").
        // Use the LAST " - " so multi-dash titles still split cleanly.
        var lastDash = stem.LastIndexOf(sep, StringComparison.Ordinal);
        if (MatchesAuthor(stem[(lastDash + sep.Length)..], author))
            return stem[..lastDash].Trim();

        return stem;
    }

    internal static (List<LocalBookFile> Inserts, List<LocalBookFile> Updates, HashSet<string> Processed)
        MatchAuthorFilesForTests(
            Author author,
            Dictionary<string, List<CalibreBookEntry>> entriesByFolderKey,
            Dictionary<int, List<Book>> booksByAuthorId,
            Dictionary<int, List<int>> nonPenNameChildrenByCanonical,
            Dictionary<string, LocalBookFile> existingByPath)
    {
        var processed = new HashSet<string>(StringComparer.Ordinal);
        var toInsert = new List<LocalBookFile>();
        var toUpdate = new List<LocalBookFile>();
        MatchAuthorFiles(author, entriesByFolderKey, booksByAuthorId, nonPenNameChildrenByCanonical, existingByPath, processed, toInsert, toUpdate);
        return (toInsert, toUpdate, processed);
    }

    internal static (List<int> RemovedIds, List<int> StaleOrphanIds, List<int> DefinitelyStaleIds)
        ComputeCleanupSetsForTests(
            IReadOnlyList<LocalBookFile> existingList,
            IReadOnlyDictionary<string, LocalBookFile> existingByPath,
            IReadOnlyDictionary<string, CalibreBookEntry> deduped,
            IReadOnlyCollection<int> updatedIds,
            IReadOnlyCollection<string> trackedFolderKeys,
            Func<string, bool> fileExists)
    {
        static string Canon(string p) =>
            p.Normalize(System.Text.NormalizationForm.FormC).ToUpperInvariant();

        var removedIds = existingByPath
            .Where(kvp => !deduped.ContainsKey(kvp.Key) && !updatedIds.Contains(kvp.Value.Id))
            .Select(kvp => kvp.Value.Id)
            .Distinct()
            .ToList();

        var staleOrphanIds = existingList
            .Where(f => f.AuthorId == null &&
                        !trackedFolderKeys.Contains(TitleNormalizer.NormalizeAuthor(f.AuthorFolder)))
            .Select(f => f.Id)
            .ToList();

        var definitelyStaleIds = existingList
            .Where(f => !deduped.ContainsKey(Canon(f.FullPath)))
            .Where(f => string.IsNullOrEmpty(Path.GetExtension(f.FullPath))
                         ? true
                         : !fileExists(f.FullPath))
            .Select(f => f.Id)
            .ToList();

        return (removedIds, staleOrphanIds, definitelyStaleIds);
    }

    // True when `segment` normalises (with name-variant rotations) to the
    // author's name or Calibre folder name.
    private static bool MatchesAuthor(string segment, Author author)
    {
        if (string.IsNullOrWhiteSpace(segment)) return false;
        var key = TitleNormalizer.NormalizeAuthor(segment);
        if (string.IsNullOrEmpty(key)) return false;

        foreach (var name in AuthorKeys(author))
        {
            if (string.Equals(key, name, StringComparison.Ordinal)) return true;
            foreach (var v in Services.Incoming.AuthorMatcher.ExpandNameVariants(name))
                if (string.Equals(key, v, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static void UpsertLocalFile(
        CalibreBookEntry entry,
        int? authorId,
        int? bookId,
        Dictionary<string, LocalBookFile> existingByPath,
        string canon,
        List<LocalBookFile> toInsert,
        List<LocalBookFile> toUpdate)
    {
        var norm = TitleNormalizer.Normalize(entry.TitleFolder);
        if (existingByPath.TryGetValue(canon, out var existing))
        {
            // Don't auto-match a file the user has explicitly unmatched; also
            // don't override an existing match with a newly computed one (user
            // may have manually corrected it via the match UI).
            var effectiveBookId = existing.ManuallyUnmatched ? null : (bookId ?? existing.BookId);
            if (existing.SizeBytes == entry.SizeBytes &&
                existing.ModifiedAt == entry.ModifiedAt &&
                existing.AuthorId == authorId &&
                existing.BookId == effectiveBookId &&
                existing.NormalizedTitle == norm &&
                existing.AuthorFolder == entry.AuthorFolder &&
                existing.TitleFolder == entry.TitleFolder &&
                // Include FullPath so a folder-path → file-path migration is persisted.
                string.Equals(existing.FullPath, entry.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            existing.FullPath = entry.FullPath;
            existing.AuthorFolder = entry.AuthorFolder;
            existing.TitleFolder = entry.TitleFolder;
            existing.NormalizedTitle = norm;
            existing.SizeBytes = entry.SizeBytes;
            existing.ModifiedAt = entry.ModifiedAt;
            existing.AuthorId = authorId;
            existing.BookId = effectiveBookId;
            toUpdate.Add(existing);
            return;
        }

        var row = new LocalBookFile
        {
            AuthorFolder = entry.AuthorFolder,
            TitleFolder = entry.TitleFolder,
            FullPath = entry.FullPath,
            NormalizedTitle = norm,
            AuthorId = authorId,
            BookId = bookId,
            SizeBytes = entry.SizeBytes,
            ModifiedAt = entry.ModifiedAt
        };
        toInsert.Add(row);
        existingByPath[canon] = row;
    }

    // Flushes any Author tracked changes, then bulk-writes LocalBookFile inserts and
    // updates via SqlBulkCopy + staging-table SQL so the EF change tracker never sees
    // the LBF rows at all. This handles 200k+ rows in seconds instead of minutes and
    // automatically resolves collation-fold duplicates via the MERGE ON FullPath.
    private async Task BulkUpsertLocalFilesAsync(
        List<LocalBookFile> toInsert,
        List<LocalBookFile> toUpdate,
        LibraryDbContext db,
        CancellationToken ct)
    {
        var total = toInsert.Count + toUpdate.Count;
        MutateState(s => { s.Message = $"Saving {total:N0} change(s)"; s.LocalFilesSaveTotal = total; s.LocalFilesSaved = 0; });

        // Flush Author/other tracked changes (e.g., CalibreFolderName back-fills).
        // LocalBookFiles are not in the tracker so this won't touch them.
        await db.SaveChangesAsync(ct);

        if (total == 0) return;

        var conn = (SqlConnection)db.Database.GetDbConnection();
        bool wasOpen = conn.State == ConnectionState.Open;
        if (!wasOpen) await conn.OpenAsync(ct);
        try
        {
            if (toUpdate.Count > 0)
            {
                MutateState(s => s.Message = $"Updating {toUpdate.Count:N0} file record(s)…");
                await BulkUpdateLocalFilesAsync(toUpdate, conn, ct);
                MutateState(s => s.LocalFilesSaved = toUpdate.Count);
            }

            if (toInsert.Count > 0)
            {
                MutateState(s => s.Message = $"Inserting {toInsert.Count:N0} new file record(s)…");
                await BulkInsertLocalFilesAsync(toInsert, conn, ct);
                MutateState(s => s.LocalFilesSaved = toUpdate.Count + toInsert.Count);
            }
        }
        finally
        {
            if (!wasOpen) conn.Close();
        }
    }

    private static async Task BulkUpdateLocalFilesAsync(
        List<LocalBookFile> updates, SqlConnection conn, CancellationToken ct)
    {
        await using (var cmd = new SqlCommand(@"
            CREATE TABLE #lbf_upd (
                Id              int             NOT NULL PRIMARY KEY,
                FullPath        nvarchar(2048)  NOT NULL,
                AuthorFolder    nvarchar(1024)  NOT NULL,
                TitleFolder     nvarchar(1024)  NOT NULL,
                NormalizedTitle nvarchar(1024),
                AuthorId        int,
                BookId          int,
                SizeBytes       bigint          NOT NULL,
                ModifiedAt      datetime2       NOT NULL
            )", conn))
            await cmd.ExecuteNonQueryAsync(ct);

        var dt = new DataTable();
        dt.Columns.Add("Id",              typeof(int));
        dt.Columns.Add("FullPath",        typeof(string));
        dt.Columns.Add("AuthorFolder",    typeof(string));
        dt.Columns.Add("TitleFolder",     typeof(string));
        dt.Columns.Add("NormalizedTitle", typeof(string));
        dt.Columns.Add("AuthorId",        typeof(int));
        dt.Columns.Add("BookId",          typeof(int));
        dt.Columns.Add("SizeBytes",       typeof(long));
        dt.Columns.Add("ModifiedAt",      typeof(DateTime));
        foreach (var r in updates.DistinctBy(f => f.Id))
            dt.Rows.Add(r.Id, r.FullPath, r.AuthorFolder, r.TitleFolder,
                (object?)r.NormalizedTitle ?? DBNull.Value,
                (object?)r.AuthorId        ?? DBNull.Value,
                (object?)r.BookId          ?? DBNull.Value,
                r.SizeBytes, r.ModifiedAt);

        using var bc = new SqlBulkCopy(conn) { DestinationTableName = "#lbf_upd", BulkCopyTimeout = 600, BatchSize = 10_000 };
        foreach (DataColumn col in dt.Columns) bc.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bc.WriteToServerAsync(dt, ct);

        await using (var cmd = new SqlCommand(@"
            UPDATE f SET
                f.FullPath        = t.FullPath,
                f.AuthorFolder    = t.AuthorFolder,
                f.TitleFolder     = t.TitleFolder,
                f.NormalizedTitle = t.NormalizedTitle,
                f.AuthorId        = t.AuthorId,
                f.BookId          = t.BookId,
                f.SizeBytes       = t.SizeBytes,
                f.ModifiedAt      = t.ModifiedAt
            FROM LocalBookFiles f
            INNER JOIN #lbf_upd t ON f.Id = t.Id", conn) { CommandTimeout = 600 })
            await cmd.ExecuteNonQueryAsync(ct);

        await using (var cmd = new SqlCommand("DROP TABLE #lbf_upd", conn))
            await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task BulkInsertLocalFilesAsync(
        List<LocalBookFile> inserts, SqlConnection conn, CancellationToken ct)
    {
        await using (var cmd = new SqlCommand(@"
            CREATE TABLE #lbf_ins (
                AuthorFolder    nvarchar(1024)  COLLATE DATABASE_DEFAULT NOT NULL,
                TitleFolder     nvarchar(1024)  COLLATE DATABASE_DEFAULT NOT NULL,
                FullPath        nvarchar(2048)  COLLATE DATABASE_DEFAULT NOT NULL,
                NormalizedTitle nvarchar(1024)  COLLATE DATABASE_DEFAULT,
                AuthorId        int,
                BookId          int,
                SizeBytes       bigint          NOT NULL,
                ModifiedAt      datetime2       NOT NULL
            )", conn))
            await cmd.ExecuteNonQueryAsync(ct);

        var dt = new DataTable();
        dt.Columns.Add("AuthorFolder",    typeof(string));
        dt.Columns.Add("TitleFolder",     typeof(string));
        dt.Columns.Add("FullPath",        typeof(string));
        dt.Columns.Add("NormalizedTitle", typeof(string));
        dt.Columns.Add("AuthorId",        typeof(int));
        dt.Columns.Add("BookId",          typeof(int));
        dt.Columns.Add("SizeBytes",       typeof(long));
        dt.Columns.Add("ModifiedAt",      typeof(DateTime));
        foreach (var r in inserts)
            dt.Rows.Add(r.AuthorFolder, r.TitleFolder, r.FullPath,
                (object?)r.NormalizedTitle ?? DBNull.Value,
                (object?)r.AuthorId        ?? DBNull.Value,
                (object?)r.BookId          ?? DBNull.Value,
                r.SizeBytes, r.ModifiedAt);

        using var bc = new SqlBulkCopy(conn) { DestinationTableName = "#lbf_ins", BulkCopyTimeout = 600, BatchSize = 10_000 };
        foreach (DataColumn col in dt.Columns) bc.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bc.WriteToServerAsync(dt, ct);

        // MERGE ON FullPath: inserts new rows and, for collation-fold collisions
        // (where SQL Server's CI_AS treats two .NET-distinct paths as equal),
        // updates the existing row and overwrites its stored FullPath so future
        // scans find it without going through this fallback again.
        // rn > 1 rows are filtered out in the WHERE clause so they are excluded
        // from the source entirely — if they were instead hidden behind the ON
        // condition they would still participate as WHEN NOT MATCHED BY TARGET
        // and trigger spurious INSERTs that collide with the unique index.
        await using (var cmd = new SqlCommand(@"
            MERGE LocalBookFiles AS target
            USING (
                SELECT AuthorFolder, TitleFolder, FullPath, NormalizedTitle,
                       AuthorId, BookId, SizeBytes, ModifiedAt
                FROM (
                    SELECT AuthorFolder, TitleFolder, FullPath, NormalizedTitle,
                           AuthorId, BookId, SizeBytes, ModifiedAt,
                           ROW_NUMBER() OVER (PARTITION BY FullPath ORDER BY (SELECT NULL)) AS rn
                    FROM #lbf_ins
                ) AS ranked
                WHERE rn = 1
            ) AS source ON target.FullPath = source.FullPath
            WHEN NOT MATCHED BY TARGET THEN
                INSERT (AuthorFolder, TitleFolder, FullPath, NormalizedTitle,
                        AuthorId, BookId, SizeBytes, ModifiedAt)
                VALUES (source.AuthorFolder, source.TitleFolder, source.FullPath,
                        source.NormalizedTitle, source.AuthorId, source.BookId,
                        source.SizeBytes, source.ModifiedAt)
            WHEN MATCHED THEN UPDATE SET
                target.FullPath        = source.FullPath,
                target.AuthorFolder    = source.AuthorFolder,
                target.TitleFolder     = source.TitleFolder,
                target.NormalizedTitle = source.NormalizedTitle,
                target.AuthorId        = source.AuthorId,
                target.BookId          = source.BookId,
                target.SizeBytes       = source.SizeBytes,
                target.ModifiedAt      = source.ModifiedAt;", conn) { CommandTimeout = 600 })
            await cmd.ExecuteNonQueryAsync(ct);

        await using (var cmd = new SqlCommand("DROP TABLE #lbf_ins", conn))
            await cmd.ExecuteNonQueryAsync(ct);
    }

    private static IEnumerable<string> AuthorKeys(Author a)
    {
        if (!string.IsNullOrWhiteSpace(a.Name))
            yield return TitleNormalizer.NormalizeAuthor(a.Name);
        if (!string.IsNullOrWhiteSpace(a.CalibreFolderName))
            yield return TitleNormalizer.NormalizeAuthor(a.CalibreFolderName);
    }

    private void MutateState(Action<SyncState> mutate)
    {
        lock (_stateLock) mutate(_state);
    }

    private static SyncState Clone(SyncState s) => new()
    {
        Phase = s.Phase,
        Message = s.Message,
        AuthorsTotal = s.AuthorsTotal,
        AuthorsProcessed = s.AuthorsProcessed,
        BooksAdded = s.BooksAdded,
        LocalFilesSeen = s.LocalFilesSeen,
        LocalFilesSaveTotal = s.LocalFilesSaveTotal,
        LocalFilesSaved = s.LocalFilesSaved,
        DumpBytesDone = s.DumpBytesDone,
        DumpBytesTotal = s.DumpBytesTotal,
        DumpRowsParsed = s.DumpRowsParsed,
        DumpAuthorsInserted = s.DumpAuthorsInserted,
        UpdateDaysTotal = s.UpdateDaysTotal,
        UpdateDaysProcessed = s.UpdateDaysProcessed,
        UpdateMergesSeen = s.UpdateMergesSeen,
        UpdateAuthorsRekeyed = s.UpdateAuthorsRekeyed,
        UpdateAuthorsFolded = s.UpdateAuthorsFolded,
        UpdateCatalogInserted = s.UpdateCatalogInserted,
        UpdateCatalogUpdated = s.UpdateCatalogUpdated,
        UpdateCurrentDay = s.UpdateCurrentDay,
        StartedAt = s.StartedAt,
        FinishedAt = s.FinishedAt,
        Error = s.Error
    };

    public bool TryStartAuthorUpdates(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("author-updates", out var holder))
        {
            error = $"Another task is already running ({holder})";
            return false;
        }
        error = null;

        lock (_stateLock) _state = new SyncState
        {
            Phase = SyncPhase.AuthorUpdates,
            StartedAt = DateTime.UtcNow,
            Message = "Starting author updates"
        };

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<AuthorUpdateProcessor>();
                var result = await processor.ProcessAsync(p =>
                {
                    MutateState(s =>
                    {
                        s.Phase = SyncPhase.AuthorUpdates;
                        if (p.Message is not null) s.Message = p.Message;
                        s.UpdateDaysTotal = p.DaysTotal;
                        s.UpdateDaysProcessed = p.DaysProcessed;
                        s.UpdateMergesSeen = p.MergesSeen;
                        s.UpdateAuthorsRekeyed = p.AuthorsUpdated;
                        s.UpdateAuthorsFolded = p.AuthorsRemoved;
                        s.UpdateCatalogInserted = p.CatalogInserted;
                        s.UpdateCatalogUpdated = p.CatalogUpdated;
                        s.UpdateCurrentDay = p.CurrentDay?.ToString("yyyy-MM-dd");
                    });
                }, hostCt);

                MutateState(s =>
                {
                    s.Phase = SyncPhase.Done;
                    s.FinishedAt = DateTime.UtcNow;
                    s.Message = result.DaysProcessed == 0
                        ? "Already up to date"
                        : $"Processed {result.DaysProcessed} day(s); {result.AuthorsUpdated} rekeyed, {result.AuthorsRemoved} folded, {result.CatalogInserted} catalog inserted, {result.CatalogUpdated} catalog updated";
                });
            }
            catch (OperationCanceledException) when (hostCt.IsCancellationRequested)
            {
                _log.LogWarning("Author-updates canceled");
                MutateState(s => { s.Phase = SyncPhase.Failed; s.Error = "Canceled"; s.FinishedAt = DateTime.UtcNow; });
            }
            catch (Exception ex)
            {
                var step = GetState().Message;
                _log.LogError(ex, "Author-updates failed during step: {Step}", step);
                var flat = ExceptionFormatter.Flatten(ex);
                var full = string.IsNullOrWhiteSpace(step) ? flat : $"During \"{step}\": {flat}";
                MutateState(s => { s.Phase = SyncPhase.Failed; s.Error = full; s.FinishedAt = DateTime.UtcNow; });
            }
            finally { _coordinator.Release(); }
        }, hostCt);

        return true;
    }

    // Reads a non-negative integer AppSetting value, falling back to `fallback`
    // when the row is missing, blank, or unparseable.
    private static int ReadIntSetting(IReadOnlyDictionary<string, string> rows, string key, int fallback)
        => rows.TryGetValue(key, out var v) && int.TryParse(v, out var n) && n >= 0 ? n : fallback;

    // Walks the Authors table for anyone whose NextFetchAt is null or past and
    // re-runs AuthorRefresher per author. Null NextFetchAt goes first (never
    // refreshed), then oldest-due up. RefreshAsync saves per-author, so a
    // failure mid-run leaves earlier authors committed — matches the user's
    // requirement to persist progress incrementally.
    public bool TryStartRefreshDueWorks(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("refresh-works", out var holder))
        {
            error = $"Another task is already running ({holder})";
            return false;
        }
        error = null;

        lock (_stateLock) _state = new SyncState
        {
            Phase = SyncPhase.FetchingWorks,
            StartedAt = DateTime.UtcNow,
            Message = "Starting works refresh"
        };

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                var refresher = scope.ServiceProvider.GetRequiredService<AuthorRefresher>();

                var now = DateTime.UtcNow;

                // Operational limits, read fresh from the DB each run (Settings page).
                var limits = await db.AppSettings
                    .Where(s => s.Key == AppSettingKeys.RefreshMaxAuthorsPerRun
                             || s.Key == AppSettingKeys.RefreshEarlyWhenNoneDue
                             || s.Key == AppSettingKeys.RefreshEarlyMaxDaysAhead)
                    .ToDictionaryAsync(s => s.Key, s => s.Value, hostCt);
                int maxPerRun    = ReadIntSetting(limits, AppSettingKeys.RefreshMaxAuthorsPerRun, 0);
                int maxEarly     = ReadIntSetting(limits, AppSettingKeys.RefreshEarlyWhenNoneDue, 200);
                int earlyMaxDays = ReadIntSetting(limits, AppSettingKeys.RefreshEarlyMaxDaysAhead, 0);

                // Authors actually due, oldest first. Pending authors always
                // count as due — Status=Pending means the row has never been
                // successfully refreshed, so an out-of-band NextFetchAt (e.g.
                // a linked/collision deferral) must not hide them from the run.
                var authorIds = await db.Authors
                    .Where(a => a.Status == AuthorStatus.Pending
                             || a.NextFetchAt == null
                             || a.NextFetchAt <= now)
                    .OrderByDescending(a => a.Status == AuthorStatus.Pending) // Pending first
                    .ThenBy(a => a.NextFetchAt.HasValue) // then nulls
                    .ThenBy(a => a.NextFetchAt)
                    .Select(a => a.Id)
                    .ToListAsync(hostCt);

                // Nothing due (and no Pending rows waiting) → pull up to
                // `maxEarly` of the soonest-due authors forward so the run
                // still does useful work. The Pending filter above ensures
                // we never fall into the early branch while pending items
                // are still unprocessed.
                // If earlyMaxDays > 0, only authors due within that many days
                // are eligible for early refreshes.
                int earlyCount = 0;
                if (authorIds.Count == 0 && maxEarly > 0)
                {
                    var earlyDeadline = earlyMaxDays > 0
                        ? now.AddDays(earlyMaxDays)
                        : (DateTime?)null;

                    var earlyQuery = db.Authors
                        .Where(a => a.NextFetchAt > now && a.Status != AuthorStatus.Pending);

                    if (earlyDeadline.HasValue)
                        earlyQuery = earlyQuery.Where(a => a.NextFetchAt <= earlyDeadline.Value);

                    authorIds = await earlyQuery
                        .OrderBy(a => a.NextFetchAt)
                        .Select(a => a.Id)
                        .Take(maxEarly)
                        .ToListAsync(hostCt);
                    earlyCount = authorIds.Count;
                }

                // Cap the whole run (0 = no limit).
                if (maxPerRun > 0 && authorIds.Count > maxPerRun)
                {
                    authorIds = authorIds.Take(maxPerRun).ToList();
                    if (earlyCount > maxPerRun) earlyCount = maxPerRun;
                }

                MutateState(s =>
                {
                    s.Phase = SyncPhase.FetchingWorks;
                    s.AuthorsTotal = authorIds.Count;
                    s.AuthorsProcessed = 0;
                    s.Message = authorIds.Count == 0
                        ? "No authors due for refresh"
                        : earlyCount > 0
                            ? $"Refreshing {authorIds.Count} author(s) ({earlyCount} pulled early — none were due)"
                            : $"Refreshing {authorIds.Count} due author(s)";
                });

                foreach (var id in authorIds)
                {
                    hostCt.ThrowIfCancellationRequested();
                    // Reload per iteration — RefreshAsync may merge/delete the
                    // row, and we want a fresh change-tracker state each time.
                    var author = await db.Authors.FirstOrDefaultAsync(a => a.Id == id, hostCt);
                    if (author is null) { MutateState(s => s.AuthorsProcessed++); continue; }

                    AuthorRefreshOutcome outcome;
                    try
                    {
                        outcome = await refresher.RefreshAsync(
                            author,
                            msg => MutateState(s => s.Message = msg),
                            hostCt);
                    }
                    catch (AuthorRefreshAlreadyRunningException ex)
                    {
                        _log.LogInformation(ex,
                            "Skipping works refresh for author {AuthorId} because another refresh is already in progress",
                            id);
                        MutateState(s => s.AuthorsProcessed++);
                        continue;
                    }
                    MutateState(s =>
                    {
                        s.BooksAdded += outcome.BooksAdded;
                        s.AuthorsProcessed++;
                    });
                }

                MutateState(s =>
                {
                    s.Phase = SyncPhase.Done;
                    s.FinishedAt = DateTime.UtcNow;
                    s.Message = authorIds.Count == 0
                        ? "No authors were due"
                        : earlyCount > 0
                            ? $"Refreshed {authorIds.Count} author(s) ({earlyCount} pulled early); {s.BooksAdded} new book(s)"
                            : $"Refreshed {authorIds.Count} author(s); {s.BooksAdded} new book(s)";
                });
            }
            catch (OperationCanceledException) when (hostCt.IsCancellationRequested)
            {
                _log.LogWarning("Refresh-due-works canceled");
                MutateState(s => { s.Phase = SyncPhase.Failed; s.Error = "Canceled"; s.FinishedAt = DateTime.UtcNow; });
            }
            catch (Exception ex)
            {
                var step = GetState().Message;
                _log.LogError(ex, "Refresh-due-works failed during step: {Step}", step);
                var flat = ExceptionFormatter.Flatten(ex);
                var full = string.IsNullOrWhiteSpace(step) ? flat : $"During \"{step}\": {flat}";
                MutateState(s => { s.Phase = SyncPhase.Failed; s.Error = full; s.FinishedAt = DateTime.UtcNow; });
            }
            finally { _coordinator.Release(); }
        }, hostCt);

        return true;
    }

    public bool TryStartSeed(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("seed", out var holder))
        {
            error = $"Another task is already running ({holder})";
            return false;
        }
        error = null;

        lock (_stateLock) _state = new SyncState { Phase = SyncPhase.SeedingAuthors, StartedAt = DateTime.UtcNow, Message = "Starting" };

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var seeder = scope.ServiceProvider.GetRequiredService<OpenLibrary.AuthorDumpSeeder>();
                await seeder.SeedAsync(p =>
                {
                    MutateState(s =>
                    {
                        s.Phase = SyncPhase.SeedingAuthors;
                        s.Message = p.Stage;
                        s.DumpBytesDone = p.DownloadedBytes;
                        s.DumpBytesTotal = p.TotalBytes;
                        s.DumpRowsParsed = p.Parsed;
                        s.DumpAuthorsInserted = p.Inserted;
                    });
                }, hostCt);

                MutateState(s =>
                {
                    s.Phase = SyncPhase.Done;
                    s.FinishedAt = DateTime.UtcNow;
                    s.Message = $"Seeded {s.DumpAuthorsInserted:N0} authors";
                });
            }
            catch (OperationCanceledException) when (hostCt.IsCancellationRequested)
            {
                _log.LogWarning("Author seed canceled");
                MutateState(s => { s.Phase = SyncPhase.Failed; s.Error = "Canceled"; s.FinishedAt = DateTime.UtcNow; });
            }
            catch (Exception ex)
            {
                var step = GetState().Message;
                _log.LogError(ex, "Author seed failed during step: {Step}", step);
                var flat = ExceptionFormatter.Flatten(ex);
                var full = string.IsNullOrWhiteSpace(step) ? flat : $"During \"{step}\": {flat}";
                MutateState(s => { s.Phase = SyncPhase.Failed; s.Error = full; s.FinishedAt = DateTime.UtcNow; });
            }
            finally { _coordinator.Release(); }
        }, hostCt);

        return true;
    }
}

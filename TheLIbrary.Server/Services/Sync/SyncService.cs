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
            catch (OperationCanceledException)
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
        var refresher = scope.ServiceProvider.GetRequiredService<AuthorRefresher>();

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
        await ProcessAuthorsAsync(db, refresher, entries, ct);

        // Phase 4: prune stale LocalBookFile rows.
        var prunedCount = await db.LocalBookFiles
            .Where(f => f.LastSeenAt < syncStartedUtc)
            .ExecuteDeleteAsync(ct);
        if (prunedCount > 0)
            _log.LogInformation("Pruned {Count} stale LocalBookFile row(s)", prunedCount);

        MutateState(s =>
        {
            s.Phase = SyncPhase.Done;
            s.FinishedAt = DateTime.UtcNow;
            s.Message = "Complete";
        });
    }

    // Walks every unique Calibre author folder and makes sure a DB Author row
    // exists for it. Blacklist wins (we must not silently re-create rows the
    // user just deleted). When a brand-new row is needed, try to stamp an OL
    // key from the local OpenLibraryAuthors table so Phase 3 can skip the
    // OL search call for this author — avoiding one rate-limited round trip
    // per auto-registered folder.
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
        foreach (var group in folderGroups)
        {
            ct.ThrowIfCancellationRequested();
            var folder = group.Key;
            var folderKey = TitleNormalizer.NormalizeAuthor(folder);
            MutateState(s => { s.Phase = SyncPhase.ResolvingAuthors; s.Message = $"Registering authors from Calibre folders - {folder}"; });

            if (blacklistedNormalized.Contains(folderKey))
            {
                _log.LogDebug("Skipping blacklisted author folder {Folder}", folder);
                continue;
            }

            // Include locally-tracked Added entities so two folder spellings in
            // the same run (e.g. "Kyle Mills" and "Mills, Kyle") collapse to one.
            var candidates = dbAuthors.Concat(
                db.ChangeTracker.Entries<Author>()
                  .Where(e => e.State == EntityState.Added)
                  .Select(e => e.Entity));

            var existing = candidates.FirstOrDefault(a =>
                string.Equals(a.CalibreFolderName, folder, StringComparison.OrdinalIgnoreCase) ||
                TitleNormalizer.NormalizeAuthor(a.Name) == folderKey ||
                TitleNormalizer.NormalizeAuthor(a.CalibreFolderName) == folderKey);

            if (existing is not null)
            {
                if (string.IsNullOrEmpty(existing.CalibreFolderName))
                    existing.CalibreFolderName = folder;
                continue;
            }

            // New folder — try local OL-verify before creating the row.
            string? olKey = null;
            string authorName = folder;
            foreach (var probe in AuthorMatcher.AuthorKeyVariants(folder))
            {
                var hit = await db.OpenLibraryAuthors
                    .AsNoTracking()
                    .Where(a => a.NormalizedName == probe)
                    .OrderBy(a => a.Id)
                    .Select(a => new { a.Name, a.OlKey })
                    .FirstOrDefaultAsync(ct);
                if (hit is null) continue;

                var hitKey = TitleNormalizer.NormalizeAuthor(hit.Name);
                if (blacklistedNormalized.Contains(hitKey)) continue;

                olKey = hit.OlKey;
                authorName = hit.Name;
                break;
            }

            // If some other folder already pre-stamped this OL key this run,
            // attach the current folder to that canonical row instead of
            // creating a duplicate (the unique OL-key index would reject it).
            if (olKey is not null)
            {
                var canonical = candidates.FirstOrDefault(a => a.OpenLibraryKey == olKey);
                if (canonical is not null)
                {
                    if (string.IsNullOrEmpty(canonical.CalibreFolderName))
                        canonical.CalibreFolderName = folder;
                    continue;
                }
            }

            db.Authors.Add(new Author
            {
                Name = authorName,
                CalibreFolderName = folder,
                OpenLibraryKey = olKey,
                Status = AuthorStatus.Pending
            });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    // Unified per-author pass. Walks every DB author and, for each:
    //   - if NextFetchAt is null or in the past, calls AuthorRefresher (OL
    //     key resolution + works fetch + schedule update) — the rate-limited
    //     piece, still one-author-at-a-time;
    //   - always links that author's Calibre files to their books in the
    //     same iteration, using the just-saved Books rows for due authors
    //     and the existing rows otherwise.
    // Orphan Calibre entries (folders with no matching Author — typically
    // blacklisted ones) fall through to a final unclaimed pass so they
    // still surface in the UI.
    private async Task ProcessAuthorsAsync(
        LibraryDbContext db,
        AuthorRefresher refresher,
        IReadOnlyList<CalibreBookEntry> entries,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        // Longest-waiting first: null CalibreScannedAt (never processed) leads,
        // then oldest-scanned, so interrupted runs always catch up stragglers
        // before re-scanning authors that were done recently.
        var authors = await db.Authors
            .OrderBy(a => a.CalibreScannedAt.HasValue)   // false (null) sorts first
            .ThenBy(a => a.CalibreScannedAt)
            .ToListAsync(ct);
        var dueCount = authors.Count(a => a.NextFetchAt == null || a.NextFetchAt <= now);

        MutateState(s =>
        {
            s.Phase = SyncPhase.FetchingWorks;
            s.AuthorsTotal = authors.Count;
            s.AuthorsProcessed = 0;
            s.Message = dueCount == authors.Count
                ? $"Processing {authors.Count} author(s)"
                : $"Processing {authors.Count} author(s); {dueCount} due for OL refresh, {authors.Count - dueCount} match-only";
        });

        // Pre-index Calibre entries by normalized author-folder key so each
        // per-author pass picks up its files in O(1).
        static string Canon(string p) =>
            p.Normalize(System.Text.NormalizationForm.FormC).ToUpperInvariant();

        var deduped = new Dictionary<string, CalibreBookEntry>(StringComparer.Ordinal);
        foreach (var e in entries) deduped[Canon(e.FullPath)] = e;

        var entriesByFolderKey = deduped.Values
            .GroupBy(e => TitleNormalizer.NormalizeAuthor(e.AuthorFolder), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var existingList = await db.LocalBookFiles.ToListAsync(ct);
        var existingByPath = new Dictionary<string, LocalBookFile>(StringComparer.Ordinal);
        foreach (var f in existingList) existingByPath[Canon(f.FullPath)] = f;

        var processed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var author in authors)
        {
            ct.ThrowIfCancellationRequested();

            var due = author.NextFetchAt == null || author.NextFetchAt <= now;
            if (due)
            {
                var outcome = await refresher.RefreshAsync(
                    author,
                    msg => MutateState(s => s.Message = msg),
                    ct);
                MutateState(s => s.BooksAdded += outcome.BooksAdded);

                // RefreshAsync may have merged this row into a canonical one
                // (same OL key, different folder spelling). That row is gone;
                // its files will link via the canonical author's folder keys.
                if (outcome.MergedIntoCanonical)
                {
                    MutateState(s => s.AuthorsProcessed++);
                    continue;  // author row was deleted — can't stamp CalibreScannedAt
                }
            }
            else
            {
                MutateState(s => s.Message = $"Matching local files for {author.Name}");
            }

            await MatchAuthorFilesAsync(db, author, entriesByFolderKey, existingByPath, processed, ct);

            author.CalibreScannedAt = DateTime.UtcNow;

            // Flush LocalBookFile changes now — RefreshAsync for the NEXT
            // author calls SaveChangesAsync internally, and if there are any
            // LBF adds still pending in the change tracker they get flushed
            // too, bypassing the resilient per-row retry and blowing up on
            // SQL's CI_AS path collation folds (NFC+ToUpper doesn't catch
            // every fold SQL applies).
            await SaveLocalFilesResilientAsync(db, ct);
            MutateState(s => s.AuthorsProcessed++);
        }

        // Orphan pass: entries whose folder resolved to no author (blacklisted
        // or otherwise) still need LocalBookFile rows with AuthorId=null so
        // the "unclaimed" UI can show them.
        MutateState(s => s.Message = "Recording orphan entries");
        foreach (var (canon, entry) in deduped)
        {
            if (processed.Contains(canon)) continue;
            UpsertLocalFile(db, entry, authorId: null, bookId: null, existingByPath, canon);
        }

        MutateState(s => s.Message = "Saving local file rows");
        await SaveLocalFilesResilientAsync(db, ct);
    }

    private async Task MatchAuthorFilesAsync(
        LibraryDbContext db,
        Author author,
        Dictionary<string, List<CalibreBookEntry>> entriesByFolderKey,
        Dictionary<string, LocalBookFile> existingByPath,
        HashSet<string> processed,
        CancellationToken ct)
    {
        static string Canon(string p) =>
            p.Normalize(System.Text.NormalizationForm.FormC).ToUpperInvariant();

        // An author can own multiple folder spellings — match any folder whose
        // normalized name hits either Name or CalibreFolderName.
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in AuthorKeys(author)) keys.Add(k);

        var relevantEntries = new List<CalibreBookEntry>();
        foreach (var key in keys)
            if (entriesByFolderKey.TryGetValue(key, out var list))
                relevantEntries.AddRange(list);

        if (relevantEntries.Count == 0) return;

        // Load this author's books (now possibly just-fetched by RefreshAsync).
        var books = await db.Books.Where(b => b.AuthorId == author.Id).ToListAsync(ct);
        var bookByTitle = new Dictionary<string, Book>(StringComparer.Ordinal);
        foreach (var b in books)
        {
            var t = b.NormalizedTitle ?? "";
            if (!bookByTitle.ContainsKey(t)) bookByTitle[t] = b;
        }

        foreach (var entry in relevantEntries)
        {
            ct.ThrowIfCancellationRequested();
            var norm = TitleNormalizer.Normalize(entry.TitleFolder);
            bookByTitle.TryGetValue(norm, out var matchedBook);

            var canon = Canon(entry.FullPath);
            UpsertLocalFile(db, entry, author.Id, matchedBook?.Id, existingByPath, canon);
            processed.Add(canon);
        }
    }

    private static void UpsertLocalFile(
        LibraryDbContext db,
        CalibreBookEntry entry,
        int? authorId,
        int? bookId,
        Dictionary<string, LocalBookFile> existingByPath,
        string canon)
    {
        var norm = TitleNormalizer.Normalize(entry.TitleFolder);
        if (existingByPath.TryGetValue(canon, out var existing))
        {
            existing.AuthorFolder = entry.AuthorFolder;
            existing.TitleFolder = entry.TitleFolder;
            existing.NormalizedTitle = norm;
            existing.AuthorId = authorId;
            existing.BookId = bookId ?? existing.BookId;
            existing.LastSeenAt = DateTime.UtcNow;
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
            LastSeenAt = DateTime.UtcNow
        };
        db.LocalBookFiles.Add(row);
        existingByPath[canon] = row;
    }

    // SQL Server's collation rules can fold characters in ways no standard
    // .NET comparer mirrors exactly, so despite best-effort dedupe a pair
    // of paths may still collide at SaveChanges. When that happens, detach
    // the offenders and retry the remaining adds one at a time; a single
    // broken pair then costs one skipped row instead of the whole phase.
    private async Task SaveLocalFilesResilientAsync(LibraryDbContext db, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); return; }
        catch (DbUpdateException ex)
        {
            _log.LogWarning(ex, "Bulk save of LocalBookFiles hit a collision; retrying per-row");
        }

        var pending = db.ChangeTracker.Entries<LocalBookFile>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified)
            .ToList();
        foreach (var entry in pending) entry.State = EntityState.Detached;

        foreach (var entry in pending)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (entry.Entity.Id == 0) db.LocalBookFiles.Add(entry.Entity);
                else db.LocalBookFiles.Update(entry.Entity);
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                _log.LogWarning("Skipped LocalBookFile row for {Path}: {Message}",
                    entry.Entity.FullPath, ex.InnerException?.Message ?? ex.Message);
                db.Entry(entry.Entity).State = EntityState.Detached;
            }
        }
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
        DumpBytesDone = s.DumpBytesDone,
        DumpBytesTotal = s.DumpBytesTotal,
        DumpRowsParsed = s.DumpRowsParsed,
        DumpAuthorsInserted = s.DumpAuthorsInserted,
        UpdateDaysTotal = s.UpdateDaysTotal,
        UpdateDaysProcessed = s.UpdateDaysProcessed,
        UpdateMergesSeen = s.UpdateMergesSeen,
        UpdateAuthorsRekeyed = s.UpdateAuthorsRekeyed,
        UpdateAuthorsFolded = s.UpdateAuthorsFolded,
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
                        s.UpdateCurrentDay = p.CurrentDay?.ToString("yyyy-MM-dd");
                    });
                }, hostCt);

                MutateState(s =>
                {
                    s.Phase = SyncPhase.Done;
                    s.FinishedAt = DateTime.UtcNow;
                    s.Message = result.DaysProcessed == 0
                        ? "Already up to date"
                        : $"Processed {result.DaysProcessed} day(s); {result.AuthorsUpdated} rekeyed, {result.AuthorsRemoved} folded";
                });
            }
            catch (OperationCanceledException)
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
                var authorIds = await db.Authors
                    .Where(a => a.NextFetchAt == null || a.NextFetchAt <= now)
                    .OrderBy(a => a.NextFetchAt.HasValue) // false (null) first
                    .ThenBy(a => a.NextFetchAt)
                    .Select(a => a.Id)
                    .ToListAsync(hostCt);

                MutateState(s =>
                {
                    s.Phase = SyncPhase.FetchingWorks;
                    s.AuthorsTotal = authorIds.Count;
                    s.AuthorsProcessed = 0;
                    s.Message = authorIds.Count == 0
                        ? "No authors due for refresh"
                        : $"Refreshing {authorIds.Count} due author(s)";
                });

                foreach (var id in authorIds)
                {
                    hostCt.ThrowIfCancellationRequested();
                    // Reload per iteration — RefreshAsync may merge/delete the
                    // row, and we want a fresh change-tracker state each time.
                    var author = await db.Authors.FirstOrDefaultAsync(a => a.Id == id, hostCt);
                    if (author is null) { MutateState(s => s.AuthorsProcessed++); continue; }

                    var outcome = await refresher.RefreshAsync(
                        author,
                        msg => MutateState(s => s.Message = msg),
                        hostCt);
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
                        : $"Refreshed {authorIds.Count} author(s); {s.BooksAdded} new book(s)";
                });
            }
            catch (OperationCanceledException)
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
            catch (OperationCanceledException)
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

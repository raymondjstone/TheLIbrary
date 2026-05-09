using System.Data;
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

        MutateState(s =>
        {
            s.Phase = SyncPhase.Done;
            s.FinishedAt = DateTime.UtcNow;
            s.Message = "Complete";
        });
    }

    // Ensures a DB Author row exists for every new Calibre folder.
    // Existing folders are detected in O(1) via a pre-built index; only
    // genuinely new folders reach the OL-catalog probe. Blacklist wins.
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

        // Pre-build index keyed by every normalized name/folder so each lookup
        // is O(1) instead of an O(N) linear scan through all authors.
        var authorByKey = new Dictionary<string, Author>(StringComparer.Ordinal);
        var authorByOlKey = new Dictionary<string, Author>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in dbAuthors)
        {
            foreach (var k in AuthorKeys(a)) authorByKey.TryAdd(k, a);
            if (!string.IsNullOrEmpty(a.OpenLibraryKey)) authorByOlKey.TryAdd(a.OpenLibraryKey, a);
        }

        int newCount = 0;
        foreach (var group in folderGroups)
        {
            ct.ThrowIfCancellationRequested();
            var folder = group.Key;
            var folderKey = TitleNormalizer.NormalizeAuthor(folder);

            if (blacklistedNormalized.Contains(folderKey)) continue;
            if (!TitleNormalizer.IsPlausibleAuthorName(folder)) continue;

            // O(1): already known — just back-fill CalibreFolderName if blank.
            if (authorByKey.TryGetValue(folderKey, out var existing))
            {
                if (string.IsNullOrEmpty(existing.CalibreFolderName))
                    existing.CalibreFolderName = folder;
                continue;
            }

            // New folder — probe the local OL catalog before creating a row.
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

            // Another folder this run may have already claimed the same OL key.
            if (olKey is not null && authorByOlKey.TryGetValue(olKey, out var canonical))
            {
                if (string.IsNullOrEmpty(canonical.CalibreFolderName))
                    canonical.CalibreFolderName = folder;
                continue;
            }

            var newAuthor = new Author
            {
                Name = authorName,
                CalibreFolderName = folder,
                OpenLibraryKey = olKey,
                Status = AuthorStatus.Pending
            };
            db.Authors.Add(newAuthor);
            newCount++;

            // Keep the index current so later folders in this run can find the
            // newly added author without waiting for SaveChangesAsync.
            foreach (var k in AuthorKeys(newAuthor)) authorByKey.TryAdd(k, newAuthor);
            if (olKey is not null) authorByOlKey.TryAdd(olKey, newAuthor);
        }

        if (newCount > 0)
        {
            _log.LogInformation("Registering {Count} new author folder(s)", newCount);
            await db.SaveChangesAsync(ct);
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

        // Load ALL books in one query — eliminates the per-author N+1 round trip.
        var booksByAuthorId = (await db.Books.AsNoTracking().ToListAsync(ct))
            .GroupBy(b => b.AuthorId)
            .ToDictionary(g => g.Key, g => g.ToList());

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
                skipped++;
                MutateState(s => s.AuthorsProcessed++);
                continue;
            }

            MatchAuthorFiles(author, entriesByFolderKey, booksByAuthorId, existingByPath, processed, toInsert, toUpdate);
            processedAuthorIds.Add(author.Id);
            MutateState(s => s.AuthorsProcessed++);
        }

        if (skipped > 0)
            _log.LogInformation("Skipped {Count} author(s) with no file changes", skipped);

        // Orphan pass: entries whose folder resolved to no author (blacklisted
        // or otherwise) still need LocalBookFile rows with AuthorId=null so
        // the "unclaimed" UI can show them.
        MutateState(s => s.Message = "Recording orphan entries");
        foreach (var (canon, entry) in deduped)
        {
            if (processed.Contains(canon)) continue;
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
        var removedIds = existingByPath
            .Where(kvp => !deduped.ContainsKey(kvp.Key))
            .Select(kvp => kvp.Value.Id)
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
    }

    private static void MatchAuthorFiles(
        Author author,
        Dictionary<string, List<CalibreBookEntry>> entriesByFolderKey,
        Dictionary<int, List<Book>> booksByAuthorId,
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

        var bookByTitle = new Dictionary<string, Book>(StringComparer.Ordinal);
        if (booksByAuthorId.TryGetValue(author.Id, out var authorBooks))
        {
            foreach (var b in authorBooks)
            {
                var t = b.NormalizedTitle ?? "";
                if (!bookByTitle.ContainsKey(t)) bookByTitle[t] = b;
            }
        }

        foreach (var entry in relevantEntries)
        {
            var canon = Canon(entry.FullPath);
            if (processed.Contains(canon)) continue;

            Book? matchedBook = null;
            foreach (var candidate in TitleNormalizer.FolderTitleCandidates(entry.TitleFolder))
                if (bookByTitle.TryGetValue(candidate, out matchedBook)) break;

            UpsertLocalFile(entry, author.Id, matchedBook?.Id, existingByPath, canon, toInsert, toUpdate);
            processed.Add(canon);
        }
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
            var effectiveBookId = bookId ?? existing.BookId;
            if (existing.SizeBytes == entry.SizeBytes &&
                existing.ModifiedAt == entry.ModifiedAt &&
                existing.AuthorId == authorId &&
                existing.BookId == effectiveBookId &&
                existing.NormalizedTitle == norm &&
                existing.AuthorFolder == entry.AuthorFolder &&
                existing.TitleFolder == entry.TitleFolder)
            {
                return;
            }
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
        dt.Columns.Add("AuthorFolder",    typeof(string));
        dt.Columns.Add("TitleFolder",     typeof(string));
        dt.Columns.Add("NormalizedTitle", typeof(string));
        dt.Columns.Add("AuthorId",        typeof(int));
        dt.Columns.Add("BookId",          typeof(int));
        dt.Columns.Add("SizeBytes",       typeof(long));
        dt.Columns.Add("ModifiedAt",      typeof(DateTime));
        foreach (var r in updates)
            dt.Rows.Add(r.Id, r.AuthorFolder, r.TitleFolder,
                (object?)r.NormalizedTitle ?? DBNull.Value,
                (object?)r.AuthorId        ?? DBNull.Value,
                (object?)r.BookId          ?? DBNull.Value,
                r.SizeBytes, r.ModifiedAt);

        using var bc = new SqlBulkCopy(conn) { DestinationTableName = "#lbf_upd", BulkCopyTimeout = 600, BatchSize = 10_000 };
        foreach (DataColumn col in dt.Columns) bc.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bc.WriteToServerAsync(dt, ct);

        await using (var cmd = new SqlCommand(@"
            UPDATE f SET
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

                const int MinimumBatch = 100;
                var now = DateTime.UtcNow;
                var authorIds = await db.Authors
                    .Where(a => a.NextFetchAt == null || a.NextFetchAt <= now)
                    .OrderBy(a => a.NextFetchAt.HasValue) // false (null) first
                    .ThenBy(a => a.NextFetchAt)
                    .Select(a => a.Id)
                    .ToListAsync(hostCt);

                int earlyCount = 0;
                if (authorIds.Count < MinimumBatch)
                {
                    var extra = await db.Authors
                        .Where(a => a.NextFetchAt > now)
                        .OrderBy(a => a.NextFetchAt)
                        .Select(a => a.Id)
                        .Take(MinimumBatch - authorIds.Count)
                        .ToListAsync(hostCt);
                    earlyCount = extra.Count;
                    authorIds.AddRange(extra);
                }

                MutateState(s =>
                {
                    s.Phase = SyncPhase.FetchingWorks;
                    s.AuthorsTotal = authorIds.Count;
                    s.AuthorsProcessed = 0;
                    s.Message = authorIds.Count == 0
                        ? "No authors due for refresh"
                        : earlyCount > 0
                            ? $"Refreshing {authorIds.Count} author(s) ({earlyCount} pulled early to reach minimum of {MinimumBatch})"
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

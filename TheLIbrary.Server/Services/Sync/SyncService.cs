using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.AuthorUpdates;
using TheLibrary.Server.Services.Calibre;
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

        // Phase 2: ensure an Author row for every Calibre author folder.
        MutateState(s => { s.Phase = SyncPhase.ResolvingAuthors; s.Message = "Registering authors from Calibre folders"; });
        var folderGroups = entries
            .GroupBy(e => e.AuthorFolder, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Load once; candidate set for this phase is (db rows) ∪ (Added entries in this run).
        var dbAuthors = await db.Authors.ToListAsync(ct);
        foreach (var group in folderGroups)
        {
            ct.ThrowIfCancellationRequested();
            var folder = group.Key;
            var folderKey = TitleNormalizer.NormalizeAuthor(folder);
            MutateState(s => { s.Phase = SyncPhase.ResolvingAuthors; s.Message = $"Registering authors from Calibre folders - {group.Key}"; });

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

            if (existing is null)
            {
                db.Authors.Add(new Author
                {
                    Name = folder,
                    CalibreFolderName = folder,
                    Status = AuthorStatus.Pending
                });
            }
            else if (string.IsNullOrEmpty(existing.CalibreFolderName))
            {
                existing.CalibreFolderName = folder;
            }
        }
        await db.SaveChangesAsync(ct);

        // Phase 3: resolve OL key + fetch works for every tracked author that
        // is due. New authors (NextFetchAt == null) always run; scheduled
        // authors run once their next-fetch time has elapsed.
        var now = DateTime.UtcNow;
        var authors = await db.Authors
            .Where(a => a.NextFetchAt == null || a.NextFetchAt <= now)
            .ToListAsync(ct);
        var skipped = await db.Authors.CountAsync(a => a.NextFetchAt != null && a.NextFetchAt > now, ct);
        MutateState(s =>
        {
            s.Phase = SyncPhase.FetchingWorks;
            s.AuthorsTotal = authors.Count;
            s.AuthorsProcessed = 0;
            s.Message = skipped > 0
                ? $"Fetching works for {authors.Count} author(s); {skipped} not yet due"
                : $"Fetching works for {authors.Count} author(s)";
        });

        foreach (var author in authors)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await refresher.RefreshAsync(
                author,
                msg => MutateState(s => s.Message = msg),
                ct);
            MutateState(s =>
            {
                s.AuthorsProcessed++;
                s.BooksAdded += outcome.BooksAdded;
            });
        }

        // Phase 4: link Calibre folders/files to authors and books.
        MutateState(s =>
        {
            s.Phase = SyncPhase.Matching;
            s.Message = "Matching local files";
            s.AuthorsTotal = 0;
            s.AuthorsProcessed = 0;
        });
        await MatchLocalEntriesAsync(db, entries, ct);

        // Phase 5: prune stale LocalBookFile rows.
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

    private async Task MatchLocalEntriesAsync(LibraryDbContext db, IReadOnlyList<CalibreBookEntry> entries, CancellationToken ct)
    {
        if (entries.Count == 0) return;

        // SQL Server's default CI_AS collation folds characters that
        // neither OrdinalIgnoreCase nor InvariantCultureIgnoreCase catch
        // (composed vs decomposed Unicode, compatibility forms, varying
        // representations of the same codepoint). Key the dict by a
        // canonical form — NFC normalized + uppercase-invariant — so any
        // two paths SQL will consider equal collapse to the same slot.
        static string Canon(string p) =>
            p.Normalize(System.Text.NormalizationForm.FormC).ToUpperInvariant();

        MutateState(s => s.Message = "Matching: loading author and book index");

        var deduped = new Dictionary<string, CalibreBookEntry>(StringComparer.Ordinal);
        foreach (var e in entries) deduped[Canon(e.FullPath)] = e;

        var authors = await db.Authors.ToListAsync(ct);
        var authorByKey = new Dictionary<string, Author>(StringComparer.Ordinal);
        foreach (var a in authors)
            foreach (var key in AuthorKeys(a))
                if (!authorByKey.ContainsKey(key)) authorByKey[key] = a;

        var books = await db.Books.ToListAsync(ct);
        var bookByKey = books
            .GroupBy(b => (b.AuthorId, b.NormalizedTitle ?? ""))
            .ToDictionary(g => g.Key, g => g.First());

        var existingList = await db.LocalBookFiles.ToListAsync(ct);
        var existingByPath = new Dictionary<string, LocalBookFile>(StringComparer.Ordinal);
        foreach (var f in existingList) existingByPath[Canon(f.FullPath)] = f;

        var groups = deduped.Values
            .GroupBy(e => e.AuthorFolder, StringComparer.OrdinalIgnoreCase)
            .ToList();
        MutateState(s => { s.AuthorsTotal = groups.Count; s.AuthorsProcessed = 0; });

        foreach (var group in groups)
        {
            var authorFolder = group.Key;
            var folderKey = TitleNormalizer.NormalizeAuthor(authorFolder);
            authorByKey.TryGetValue(folderKey, out var matchedAuthor);
            MutateState(s => s.Message = $"Matching: {authorFolder}");

            foreach (var entry in group)
            {
                var norm = TitleNormalizer.Normalize(entry.TitleFolder);
                Book? matchedBook = null;
                if (matchedAuthor is not null)
                    bookByKey.TryGetValue((matchedAuthor.Id, norm), out matchedBook);

                var canon = Canon(entry.FullPath);
                existingByPath.TryGetValue(canon, out var existing);

                if (existing is null)
                {
                    var row = new LocalBookFile
                    {
                        AuthorFolder = entry.AuthorFolder,
                        TitleFolder = entry.TitleFolder,
                        FullPath = entry.FullPath,
                        NormalizedTitle = norm,
                        AuthorId = matchedAuthor?.Id,
                        BookId = matchedBook?.Id,
                        LastSeenAt = DateTime.UtcNow
                    };
                    db.LocalBookFiles.Add(row);
                    existingByPath[canon] = row;
                }
                else
                {
                    existing.AuthorFolder = entry.AuthorFolder;
                    existing.TitleFolder = entry.TitleFolder;
                    existing.NormalizedTitle = norm;
                    existing.AuthorId = matchedAuthor?.Id;
                    existing.BookId = matchedBook?.Id ?? existing.BookId;
                    existing.LastSeenAt = DateTime.UtcNow;
                }
            }
            MutateState(s => s.AuthorsProcessed++);
        }

        MutateState(s => s.Message = "Matching: saving local file rows");
        await SaveLocalFilesResilientAsync(db, ct);
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

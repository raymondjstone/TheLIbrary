using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.IO;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

public sealed record AuthorDisambiguationSummary(
    int GroupsProcessed,
    int AuthorsRenamed,
    int FilesMoved,
    int FilesOrphaned,
    IReadOnlyList<string> Warnings);

public sealed record AuthorDisambiguationPreviewItem(
    int FileId,
    int CurrentAuthorId,
    string CurrentFolder,
    int TargetAuthorId,
    string TargetFolder,
    string FullPath,
    bool OrphanFallback);

public sealed record AuthorDisambiguationPreview(
    int GroupsProcessed,
    int AuthorsRenamed,
    int FilesToMove,
    int FilesOrphaned,
    IReadOnlyList<AuthorDisambiguationPreviewItem> Moves,
    IReadOnlyList<string> Warnings);

// Scans the watchlist for groups of unlinked authors sharing a normalised
// name AND for which every member has an OpenLibrary key. For each such group:
//
//   * Compute each member's target folder ("<Name>_<OLKey>") via
//     AuthorFolderNameResolver.
//   * For every enabled library location, locate the merged source folder
//     (whatever name the LocalBookFile rows currently use under that root).
//   * Distribute files into per-author target folders by matching
//     LocalBookFile.NormalizedTitle against each author's Book set. Files that
//     don't match any author's books are parked under the lowest-id author's
//     new folder (deterministic, easy to manually fix later).
//   * Update LocalBookFile.AuthorId / AuthorFolder / FullPath in lockstep and
//     bump Author.CalibreFolderName on every group member.
//
// Runs as a singleton through BackgroundTaskCoordinator so it cannot overlap
// with sync, organize, incoming, etc.
public sealed class AuthorFolderDisambiguatorService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly IFileSystem _fs;
    private readonly ILogger<AuthorFolderDisambiguatorService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;
    private AuthorDisambiguationSummary? _lastResult;

    public AuthorFolderDisambiguatorService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        IFileSystem fs,
        ILogger<AuthorFolderDisambiguatorService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _fs = fs;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;
    public AuthorDisambiguationSummary? LastResult => _lastResult;

    public async Task<AuthorDisambiguationPreview> Preview(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var (preview, _, _) = await BuildPreviewInternalAsync(db, ct);
        return preview;
    }

    internal static IReadOnlyList<IReadOnlyList<Author>> FindGroupsForTests(IReadOnlyList<Author> allAuthors)
        => AuthorFolderNameResolver.FindAllCollisionGroups(allAuthors);

    internal static Author ResolveOwnerForTests(
        LocalBookFile file,
        IReadOnlyDictionary<string, int> ownerByTitle,
        Author fallback)
        => ResolveOwner(file, ownerByTitle, fallback);

    internal void MoveFileToFolderForTests(
        LocalBookFile file,
        Author owner,
        string newFolder,
        IReadOnlyList<string> locations,
        List<string> warnings)
        => MoveFileToFolderAsync(file, owner, newFolder, locations, warnings, CancellationToken.None)
            .GetAwaiter().GetResult();

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("disambiguate-folders", out var holder))
        {
            error = $"Another task is already running ({holder})";
            return false;
        }
        error = null;
        _isRunning = true;
        _ = Task.Run(async () =>
        {
            try { _lastResult = await RunAsync(hostCt); }
            catch (OperationCanceledException) when (hostCt.IsCancellationRequested) { }
            catch (Exception ex) { _log.LogError(ex, "Author folder disambiguator failed"); }
            finally { _isRunning = false; _currentMessage = null; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    private async Task<AuthorDisambiguationSummary> RunAsync(CancellationToken ct)
    {
        _log.LogInformation("Author folder disambiguator starting");
        _currentMessage = "Starting";
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

        _log.LogInformation("Disambiguator: building preview (scanning author groups and file assignments)");
        _currentMessage = "Building preview";
        var (preview, groups, allAuthors) = await BuildPreviewInternalAsync(db, ct);
        _log.LogInformation(
            "Disambiguator: preview built — {Groups} group(s), {Files} file move(s) planned, {Orphaned} orphaned, {Renamed} author rename(s)",
            preview.GroupsProcessed, preview.FilesToMove, preview.FilesOrphaned, preview.AuthorsRenamed);
        _currentMessage = $"Moving {preview.FilesToMove} file(s) across {preview.GroupsProcessed} group(s)";

        int authorsRenamed = 0, filesMoved = 0;
        var warnings = new List<string>(preview.Warnings);

        // Fetch the enabled library root paths — these are what MoveFileToFolderAsync
        // needs to strip the lib-root prefix and compute dest paths correctly.
        // (Previously this incorrectly passed file FullPaths instead of lib roots.)
        var locationPaths = await db.LibraryLocations
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);

        // Bulk-load all files and authors needed for moves — avoids N+1 per-file DB queries.
        var fileIds = preview.Moves.Select(m => m.FileId).ToList();
        var fileDict = await db.LocalBookFiles
            .Where(f => fileIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, ct);

        var authorIds = preview.Moves.Select(m => m.TargetAuthorId).ToHashSet();
        var authorDict = allAuthors
            .Where(a => authorIds.Contains(a.Id))
            .ToDictionary(a => a.Id);

        _log.LogInformation("Disambiguator: moving {Count} file(s)", preview.Moves.Count);
        const int SaveBatchSize = 500;
        foreach (var move in preview.Moves)
        {
            ct.ThrowIfCancellationRequested();
            if (!fileDict.TryGetValue(move.FileId, out var file))
            {
                _log.LogWarning("Disambiguator: skipping file id {Id} — not found in DB", move.FileId);
                continue;
            }
            var owner = authorDict[move.TargetAuthorId];
            _log.LogDebug(
                "Disambiguator: moving file {Id} '{Path}' → author '{Folder}'{Orphan}",
                move.FileId, move.FullPath, move.TargetFolder,
                move.OrphanFallback ? " (orphan fallback)" : "");
            await MoveFileToFolderAsync(file, owner, move.TargetFolder, locationPaths, warnings, ct);
            filesMoved++;

            // Save in batches so EF Core never accumulates more than SaveBatchSize
            // dirty entities at once. This keeps memory pressure low and gives us
            // natural checkpoints to update the progress message.
            if (filesMoved % SaveBatchSize == 0)
            {
                _currentMessage = $"Moved {filesMoved}/{preview.Moves.Count} file(s) — saving batch…";
                await db.SaveChangesAsync(ct);
                _currentMessage = $"Moved {filesMoved}/{preview.Moves.Count} file(s)";
                _log.LogInformation("Disambiguator: {Moved}/{Total} file(s) moved and saved", filesMoved, preview.Moves.Count);
            }
        }

        // Reuse allAuthors already loaded during preview — no second DB round-trip.
        // Since every member in a qualifying group has an OL key, the target is
        // always "Name_OLKey" — no need to re-run Resolve() (which would scan
        // allAuthors once per member, O(n) each time).
        _currentMessage = "Updating author folder names…";
        _log.LogInformation("Disambiguator: updating CalibreFolderName for renamed authors");
        foreach (var group in groups)
        {
            foreach (var member in group)
            {
                var target = $"{member.Name}_{member.OpenLibraryKey}";
                if (!string.Equals(member.CalibreFolderName, target, StringComparison.Ordinal))
                {
                    _log.LogInformation(
                        "Disambiguator: renaming author {Id} '{Old}' → '{New}'",
                        member.Id, member.CalibreFolderName ?? member.Name, target);
                    member.CalibreFolderName = target;
                    authorsRenamed++;
                }
            }
        }

        _currentMessage = $"Saving {authorsRenamed} author rename(s) and final file batch…";
        _log.LogInformation("Disambiguator: saving {Renamed} author rename(s) and remaining file changes", authorsRenamed);
        await db.SaveChangesAsync(ct);

        _log.LogInformation("Disambiguator: cleaning up empty legacy folders");
        var enabledLocations = await db.LibraryLocations
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);

        // Collect only distinct folder names that actually existed before the run
        // (i.e. the old bare names that may now be empty) rather than iterating
        // every group and every location with a cartesian product of filesystem checks.
        var legacyFolders = groups
            .SelectMany(g => g)
            .Select(a => a.Name)                     // original bare name (pre-rename)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _currentMessage = $"Cleaning up {legacyFolders.Count} legacy folder(s)…";
        _log.LogInformation("Disambiguator: checking {Count} distinct legacy folder name(s) for cleanup", legacyFolders.Count);

        var foldersDeleted = 0;
        for (int fi = 0; fi < legacyFolders.Count; fi++)
        {
            ct.ThrowIfCancellationRequested();
            var legacy = legacyFolders[fi];
            foreach (var libRoot in enabledLocations)
            {
                var path = Path.Combine(libRoot.TrimEnd('\\', '/'), legacy);
                var wasDeleted = await TryDeleteEmptyDirectoryAsync(path, ct);
                if (wasDeleted) foldersDeleted++;
            }
            if (fi % 500 == 0 || fi == legacyFolders.Count - 1)
                _currentMessage = $"Cleaning up legacy folders — {fi + 1}/{legacyFolders.Count} checked, {foldersDeleted} removed";
        }

        if (warnings.Count > 0)
        {
            foreach (var w in warnings)
                _log.LogWarning("Disambiguator warning: {Warning}", w);
        }

        _log.LogInformation(
            "Author folder disambiguator done. Groups={Groups} Renamed={Renamed} Moved={Moved} Orphaned={Orphaned} Warnings={W}",
            groups.Count, authorsRenamed, filesMoved, preview.FilesOrphaned, warnings.Count);
        _currentMessage = $"Done — {filesMoved} file(s) moved, {authorsRenamed} author(s) renamed";

        return new AuthorDisambiguationSummary(groups.Count, authorsRenamed, filesMoved, preview.FilesOrphaned, warnings);
    }

    // Returns the group member whose books contain the file's NormalizedTitle,
    // or the fallback (lowest-id) member when no match exists.
    private static Author ResolveOwner(
        LocalBookFile file,
        IReadOnlyDictionary<string, int> ownerByTitle,
        Author fallback)
    {
        if (!string.IsNullOrEmpty(file.NormalizedTitle) &&
            ownerByTitle.TryGetValue(file.NormalizedTitle, out var ownerId))
            return new Author { Id = ownerId };
        return fallback;
    }

    // Re-points the file at the new author folder under every library root that
    // contains its source path. The on-disk move is best-effort — if the file
    // isn't where the DB thinks it is, we still update the metadata so a
    // subsequent sync converges from the actual on-disk state.
    private async Task MoveFileToFolderAsync(
        LocalBookFile file,
        Author owner,
        string newFolder,
        IReadOnlyList<string> locations,
        List<string> warnings,
        CancellationToken ct)
    {
        file.AuthorId = owner.Id;
        var oldFolder = file.AuthorFolder;
        file.AuthorFolder = newFolder;

        if (string.IsNullOrWhiteSpace(file.FullPath)) return;

        var location = locations.FirstOrDefault(l =>
            file.FullPath.StartsWith(l.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
        if (location is null) return;

        var libRoot = location.TrimEnd('\\', '/');
        var relative = file.FullPath[libRoot.Length..].TrimStart('\\', '/');
        var firstSep = relative.IndexOfAny(new[] { '\\', '/' });
        if (firstSep < 0) return;
        var remainder = relative[(firstSep + 1)..];
        var destPath = Path.Combine(libRoot, newFolder, remainder);

        // Already in the right place — update AuthorId/AuthorFolder in DB but
        // skip any filesystem operation.
        if (string.Equals(file.FullPath, destPath, StringComparison.OrdinalIgnoreCase))
        {
            _log.LogDebug("Disambiguator: file '{Path}' is already in target folder '{Folder}' — skipping disk move",
                file.FullPath, newFolder);
            return;
        }

        var destDir = Path.GetDirectoryName(destPath);

        try
        {
            if (destDir is not null) await _fs.CreateDirectoryAsync(destDir, ct);
            if (await _fs.FileExistsAsync(file.FullPath, ct))
            {
                var final = await UniqueFileAsync(destPath, ct);
                await _fs.MoveFileAsync(file.FullPath, final, overwrite: false, ct);
                _log.LogDebug("Disambiguator: moved file '{Src}' → '{Dst}'", file.FullPath, final);
                file.FullPath = final;
            }
            else if (await _fs.DirectoryExistsAsync(file.FullPath, ct))
            {
                var final = await UniqueDirectoryAsync(Path.GetDirectoryName(destPath)!, Path.GetFileName(destPath), ct);
                await _fs.MoveDirectoryAsync(file.FullPath, final, ct);
                _log.LogDebug("Disambiguator: moved directory '{Src}' → '{Dst}'", file.FullPath, final);
                file.FullPath = final;
            }
            else
            {
                _log.LogWarning(
                    "Disambiguator: source not found on disk '{Path}' — DB record updated to target path anyway",
                    file.FullPath);
                file.FullPath = destPath;
            }
        }
        catch (IOException ex)
        {
            _log.LogWarning("Disambiguator: IO error moving '{Old}' → '{New}': {Msg}", oldFolder, newFolder, ex.Message);
            warnings.Add($"{oldFolder} → {newFolder}: {ex.Message}");
        }
    }

    private async Task<string> UniqueFileAsync(string desired, CancellationToken ct)
    {
        if (!await _fs.FileExistsAsync(desired, ct) && !await _fs.DirectoryExistsAsync(desired, ct)) return desired;
        var dir = Path.GetDirectoryName(desired) ?? "";
        var stem = Path.GetFileNameWithoutExtension(desired);
        var ext  = Path.GetExtension(desired);
        for (var i = 2; i < 1000; i++)
        {
            var next = Path.Combine(dir, $"{stem}_{i}{ext}");
            if (!await _fs.FileExistsAsync(next, ct) && !await _fs.DirectoryExistsAsync(next, ct)) return next;
        }
        return Path.Combine(dir, $"{stem}_{DateTime.UtcNow:yyyyMMddHHmmss}{ext}");
    }

    private async Task<string> UniqueDirectoryAsync(string parent, string leaf, CancellationToken ct)
    {
        var candidate = Path.Combine(parent, leaf);
        if (!await _fs.DirectoryExistsAsync(candidate, ct) && !await _fs.FileExistsAsync(candidate, ct)) return candidate;
        for (var i = 2; i < 1000; i++)
        {
            var next = Path.Combine(parent, $"{leaf} ({i})");
            if (!await _fs.DirectoryExistsAsync(next, ct) && !await _fs.FileExistsAsync(next, ct)) return next;
        }
        return Path.Combine(parent, $"{leaf} ({DateTime.UtcNow:yyyyMMddHHmmss})");
    }

    private async Task<(AuthorDisambiguationPreview Preview, IReadOnlyList<IReadOnlyList<Author>> Groups, List<Author> AllAuthors)>
        BuildPreviewInternalAsync(LibraryDbContext db, CancellationToken ct)
    {
        var locations = await db.LibraryLocations
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);

        // Project only the columns needed for group detection — skip Bio, Notes,
        // navigation collections, etc. that would bloat the load significantly.
        var allAuthors = await db.Authors
            .AsNoTracking()
            .Select(a => new Author
            {
                Id                = a.Id,
                Name              = a.Name,
                OpenLibraryKey    = a.OpenLibraryKey,
                CalibreFolderName = a.CalibreFolderName,
                LinkedToAuthorId  = a.LinkedToAuthorId,
                Status            = a.Status,
            })
            .ToListAsync(ct);

        _log.LogInformation("Disambiguator preview: {Authors} author(s) loaded, scanning for collision groups…",
            allAuthors.Count);
        _currentMessage = $"Preview: scanning {allAuthors.Count} author(s) for name collisions…";

        var groups = FindGroupsForTests(allAuthors);
        _log.LogInformation("Disambiguator preview: {Groups} collision group(s) found from {Authors} author(s)",
            groups.Count, allAuthors.Count);
        _currentMessage = $"Preview: {groups.Count} collision group(s) found — loading files…";

        if (groups.Count == 0)
        {
            var empty = new AuthorDisambiguationPreview(0, 0, 0, 0, [], []);
            return (empty, groups, allAuthors);
        }

        // Collect all member author IDs across every group up-front.
        var allMemberIds = groups
            .SelectMany(g => g.Select(a => a.Id))
            .ToHashSet();

        // Precompute the target folder name for every group member directly —
        // avoids calling Resolve() per member inside the loop (which would
        // re-scan allAuthors each time). We know every member in a qualifying
        // group has an OL key, so the target is always "Name_OLKey".
        var targetFolderById = groups
            .SelectMany(g => g)
            .DistinctBy(a => a.Id)
            .ToDictionary(a => a.Id, a => $"{a.Name}_{a.OpenLibraryKey}");

        _currentMessage = "Preview: loading books in bulk…";

        // Load all books and files without an IN filter — with 22k+ member IDs
        // the SQL IN(...) parameter list would be enormous and hurt SQLite more
        // than a full-table scan. Filter to members in-memory after loading.
        var allBooks = await db.Books.AsNoTracking()
            .Where(b => b.NormalizedTitle != null)
            .Select(b => new { b.AuthorId, b.NormalizedTitle })
            .ToListAsync(ct);

        // Pre-filter to only member rows once in memory.
        var memberBooks = allBooks.Where(b => allMemberIds.Contains(b.AuthorId)).ToList();

        _currentMessage = $"Preview: loaded {memberBooks.Count} book(s), loading files…";

        // Archived files are inert (see ArchivePolicy) — never relocate a copy the
        // user has archived, or it gets pulled back into a live author folder.
        var archiveLeaf = await ArchivePolicy.LoadLeafAsync(db, ct);
        var allFilesRaw = await db.LocalBookFiles.AsNoTracking()
            .Where(f => f.AuthorId != null)
            .Where(ArchivePolicy.NotUnder(archiveLeaf))
            .ToListAsync(ct);

        var allFiles = allFilesRaw.Where(f => allMemberIds.Contains(f.AuthorId!.Value)).ToList();

        _log.LogInformation("Disambiguator preview: {Books} book(s), {Files} file(s) loaded for {Members} member author(s)",
            memberBooks.Count, allFiles.Count, allMemberIds.Count);
        _currentMessage = $"Preview: {allFiles.Count} file(s) across {allMemberIds.Count} authors — loading orphans…";

        // Load ALL null-AuthorId files once and build an in-memory lookup keyed
        // by AuthorFolder. This avoids generating a SQL IN(...) with potentially
        // tens-of-thousands of folder names that would cripple SQLite.
        var allOrphansRaw = await db.LocalBookFiles.AsNoTracking()
            .Where(f => f.AuthorId == null && f.AuthorFolder != null)
            .Where(ArchivePolicy.NotUnder(archiveLeaf))
            .ToListAsync(ct);
        var orphansByFolder = allOrphansRaw
            .GroupBy(f => f.AuthorFolder!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Group the bulk results by authorId for O(1) lookup per group.
        var booksByAuthorId = memberBooks
            .GroupBy(b => b.AuthorId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var filesByAuthorId = allFiles
            .GroupBy(f => f.AuthorId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var moves        = new List<AuthorDisambiguationPreviewItem>();
        var warnings     = new List<string>();
        var authorsRenamed = 0;
        var filesOrphaned  = 0;
        var skippedEmpty   = 0;

        for (int gi = 0; gi < groups.Count; gi++)
        {
            var group = groups[gi];
            ct.ThrowIfCancellationRequested();

            var sortedByLowestId = group.OrderBy(a => a.Id).ToList();
            var fallback = sortedByLowestId[0];

            // Count renames needed for this group.
            var groupRenames = 0;
            foreach (var member in sortedByLowestId)
            {
                var target = targetFolderById[member.Id];
                if (!string.Equals(member.CalibreFolderName, target, StringComparison.Ordinal))
                    groupRenames++;
            }

            // Collect files (authored + orphans via in-memory folder lookup).
            var groupFolderCandidates = sortedByLowestId
                .SelectMany(a => new[] { a.Name, a.CalibreFolderName })
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var files = sortedByLowestId
                .SelectMany(a => filesByAuthorId.TryGetValue(a.Id, out var f) ? f : [])
                .ToList();

            var orphans = groupFolderCandidates
                .SelectMany(folder => orphansByFolder.TryGetValue(folder, out var o) ? o : [])
                .ToList();
            files.AddRange(orphans);

            // Skip groups that need nothing done — no file moves, no renames.
            // These are the bulk of the 22k+ groups where authors have no local
            // files and their CalibreFolderName is already correct.
            if (files.Count == 0 && groupRenames == 0)
            {
                skippedEmpty++;
                continue;
            }

            authorsRenamed += groupRenames;

            // Build owner-by-title map from pre-loaded books.
            var ownerByTitle = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var memberId in sortedByLowestId.Select(a => a.Id))
            {
                if (!booksByAuthorId.TryGetValue(memberId, out var authorBooks)) continue;
                foreach (var b in authorBooks.OrderBy(x => x.AuthorId))
                    ownerByTitle.TryAdd(b.NormalizedTitle!, memberId);
            }

            _log.LogDebug("Disambiguator preview: group {Index}/{Total} '{Name}' — {Files} file(s), {Renames} rename(s)",
                gi + 1, groups.Count, group.First().Name, files.Count, groupRenames);

            // Throttle UI message to avoid 22k+ writes per preview pass.
            if (gi % 50 == 0 || gi == groups.Count - 1)
                _currentMessage = $"Preview: analysing group {gi + 1}/{groups.Count} — {moves.Count} move(s) planned so far";

            foreach (var file in files)
            {
                var owner = ResolveOwner(file, ownerByTitle, fallback);
                var targetFolder = targetFolderById[owner.Id];

                // Skip files whose AuthorFolder already matches the target —
                // they are already in the right place and need no disk move.
                if (string.Equals(file.AuthorFolder, targetFolder, StringComparison.OrdinalIgnoreCase))
                    continue;

                var orphanFallback = owner.Id == fallback.Id &&
                    (string.IsNullOrEmpty(file.NormalizedTitle) || !ownerByTitle.ContainsKey(file.NormalizedTitle));
                if (orphanFallback) filesOrphaned++;

                moves.Add(new AuthorDisambiguationPreviewItem(
                    file.Id,
                    file.AuthorId ?? 0,
                    file.AuthorFolder,
                    owner.Id,
                    targetFolder,
                    file.FullPath,
                    orphanFallback));
            }
        }

        var activeGroups = groups.Count - skippedEmpty;
        _log.LogInformation(
            "Disambiguator preview complete: {Active} active group(s) ({Skipped} empty/no-op skipped), {Renamed} rename(s), {Files} file move(s), {Orphaned} orphaned",
            activeGroups, skippedEmpty, authorsRenamed, moves.Count, filesOrphaned);
        _currentMessage = $"Preview done — {moves.Count} file move(s) across {activeGroups} group(s) ({skippedEmpty} empty groups skipped)";

        var preview = new AuthorDisambiguationPreview(activeGroups, authorsRenamed, moves.Count, filesOrphaned, moves, warnings);
        return (preview, groups.Where((_, i) => true).ToList(), allAuthors);
    }

    private async Task<bool> TryDeleteEmptyDirectoryAsync(string path, CancellationToken ct)
    {
        if (!await _fs.DirectoryExistsAsync(path, ct)) return false;
        if (_fs.EnumerateFileSystemEntries(path).Any()) return false;
        try
        {
            await _fs.DeleteDirectoryAsync(path, recursive: false, ct);
            return true;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Best-effort cleanup could not delete empty directory '{Path}'", path);
            return false;
        }
    }
}

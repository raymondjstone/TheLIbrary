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
    public AuthorDisambiguationSummary? LastResult => _lastResult;

    public async Task<AuthorDisambiguationPreview> Preview(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        return await BuildPreviewAsync(db, ct);
    }

    internal static IReadOnlyList<IReadOnlyList<Author>> FindGroupsForTests(IReadOnlyList<Author> allAuthors)
    {
        var seenRepresentatives = new HashSet<int>();
        var groups = new List<IReadOnlyList<Author>>();
        foreach (var a in allAuthors)
        {
            var group = AuthorFolderNameResolver.FindCollisionGroup(a, allAuthors);
            if (group.Count < 2) continue;
            var rep = group.Min(x => x.Id);
            if (!seenRepresentatives.Add(rep)) continue;
            if (group.Any(x => string.IsNullOrWhiteSpace(x.OpenLibraryKey))) continue;
            groups.Add(group);
        }
        return groups;
    }

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
            finally { _isRunning = false; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    private async Task<AuthorDisambiguationSummary> RunAsync(CancellationToken ct)
    {
        _log.LogInformation("Author folder disambiguator starting");
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

        var preview = await BuildPreviewAsync(db, ct);

        int authorsRenamed = 0, filesMoved = 0;
        var warnings = new List<string>(preview.Warnings);

        foreach (var move in preview.Moves)
        {
            ct.ThrowIfCancellationRequested();
            var file = await db.LocalBookFiles.FirstOrDefaultAsync(f => f.Id == move.FileId, ct);
            if (file is null) continue;
            var owner = await db.Authors.FirstAsync(a => a.Id == move.TargetAuthorId, ct);
            await MoveFileToFolderAsync(file, owner, move.TargetFolder, preview.Moves.Select(m => m.FullPath).ToList(), warnings, ct);
            filesMoved++;
        }

        var allAuthors = await db.Authors.ToListAsync(ct);
        var groups = FindGroupsForTests(allAuthors);
        foreach (var group in groups)
        {
            var targetByAuthorId = group.ToDictionary(a => a.Id, a => AuthorFolderNameResolver.Resolve(a, allAuthors));
            foreach (var member in group)
            {
                var target = targetByAuthorId[member.Id];
                if (!string.Equals(member.CalibreFolderName, target, StringComparison.Ordinal))
                {
                    member.CalibreFolderName = target;
                    authorsRenamed++;
                }
            }
        }

        await db.SaveChangesAsync(ct);

        var enabledLocations = await db.LibraryLocations
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);
        foreach (var group in groups)
        {
            var folderCandidates = group
                .SelectMany(a => new[] { a.Name, a.CalibreFolderName })
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var libRoot in enabledLocations)
                foreach (var legacy in folderCandidates)
                    await TryDeleteEmptyDirectoryAsync(Path.Combine(libRoot.TrimEnd('\\', '/'), legacy), ct);
        }

        _log.LogInformation(
            "Author folder disambiguator done. Groups={Groups} Renamed={Renamed} Moved={Moved} Orphaned={Orphaned} Warnings={W}",
            groups.Count, authorsRenamed, filesMoved, preview.FilesOrphaned, warnings.Count);

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
        var destDir = Path.GetDirectoryName(destPath);

        try
        {
            if (destDir is not null) await _fs.CreateDirectoryAsync(destDir, ct);
            if (await _fs.FileExistsAsync(file.FullPath, ct))
            {
                var final = await UniqueFileAsync(destPath, ct);
                await _fs.MoveFileAsync(file.FullPath, final, overwrite: false, ct);
                file.FullPath = final;
            }
            else if (await _fs.DirectoryExistsAsync(file.FullPath, ct))
            {
                var final = await UniqueDirectoryAsync(Path.GetDirectoryName(destPath)!, Path.GetFileName(destPath), ct);
                await _fs.MoveDirectoryAsync(file.FullPath, final, ct);
                file.FullPath = final;
            }
            else
            {
                file.FullPath = destPath;
            }
        }
        catch (IOException ex)
        {
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

    private async Task<AuthorDisambiguationPreview> BuildPreviewAsync(LibraryDbContext db, CancellationToken ct)
    {
        var locations = await db.LibraryLocations
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);

        var allAuthors = await db.Authors.ToListAsync(ct);
        var groups = FindGroupsForTests(allAuthors);
        var moves = new List<AuthorDisambiguationPreviewItem>();
        var warnings = new List<string>();
        var authorsRenamed = 0;
        var filesOrphaned = 0;

        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var sortedByLowestId = group.OrderBy(a => a.Id).ToList();
            var fallback = sortedByLowestId[0];
            var targetByAuthorId = sortedByLowestId.ToDictionary(a => a.Id, a => AuthorFolderNameResolver.Resolve(a, allAuthors));
            var memberIds = sortedByLowestId.Select(a => a.Id).ToList();
            var books = await db.Books.AsNoTracking()
                .Where(b => memberIds.Contains(b.AuthorId) && b.NormalizedTitle != null)
                .Select(b => new { b.AuthorId, b.NormalizedTitle })
                .ToListAsync(ct);
            var ownerByTitle = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var b in books.OrderBy(x => x.AuthorId))
                if (!ownerByTitle.ContainsKey(b.NormalizedTitle!)) ownerByTitle[b.NormalizedTitle!] = b.AuthorId;

            var files = await db.LocalBookFiles.AsNoTracking()
                .Where(f => f.AuthorId != null && memberIds.Contains(f.AuthorId.Value))
                .ToListAsync(ct);

            var folderCandidates = sortedByLowestId
                .SelectMany(a => new[] { a.Name, a.CalibreFolderName })
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var orphans = await db.LocalBookFiles.AsNoTracking()
                .Where(f => f.AuthorId == null && folderCandidates.Contains(f.AuthorFolder))
                .ToListAsync(ct);
            files.AddRange(orphans);

            foreach (var file in files)
            {
                var owner = ResolveOwner(file, ownerByTitle, fallback);
                var orphanFallback = owner.Id == fallback.Id &&
                    (string.IsNullOrEmpty(file.NormalizedTitle) || !ownerByTitle.ContainsKey(file.NormalizedTitle));
                if (orphanFallback) filesOrphaned++;

                moves.Add(new AuthorDisambiguationPreviewItem(
                    file.Id,
                    file.AuthorId ?? 0,
                    file.AuthorFolder,
                    owner.Id,
                    targetByAuthorId[owner.Id],
                    file.FullPath,
                    orphanFallback));
            }

            foreach (var member in sortedByLowestId)
                if (!string.Equals(member.CalibreFolderName, targetByAuthorId[member.Id], StringComparison.Ordinal))
                    authorsRenamed++;
        }

        return new AuthorDisambiguationPreview(groups.Count, authorsRenamed, moves.Count, filesOrphaned, moves, warnings);
    }

    private async Task TryDeleteEmptyDirectoryAsync(string path, CancellationToken ct)
    {
        if (!await _fs.DirectoryExistsAsync(path, ct)) return;
        if (_fs.EnumerateFileSystemEntries(path).Any()) return;
        try
        {
            await _fs.DeleteDirectoryAsync(path, recursive: false, ct);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Best-effort cleanup could not delete empty directory '{Path}'", path);
        }
    }
}

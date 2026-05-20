using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

public sealed record AuthorDisambiguationSummary(
    int GroupsProcessed,
    int AuthorsRenamed,
    int FilesMoved,
    int FilesOrphaned,
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
    private readonly ILogger<AuthorFolderDisambiguatorService> _log;
    private volatile bool _isRunning;
    private AuthorDisambiguationSummary? _lastResult;

    public AuthorFolderDisambiguatorService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        ILogger<AuthorFolderDisambiguatorService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public AuthorDisambiguationSummary? LastResult => _lastResult;

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

        var locations = await db.LibraryLocations
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);

        var allAuthors = await db.Authors.ToListAsync(ct);

        // Build collision groups: every author with another unlinked author of
        // the same normalised name. Sets are de-duplicated by representative
        // (the lowest-id member), so each group is processed exactly once.
        var seenRepresentatives = new HashSet<int>();
        var groups = new List<IReadOnlyList<Author>>();
        foreach (var a in allAuthors)
        {
            var group = AuthorFolderNameResolver.FindCollisionGroup(a, allAuthors);
            if (group.Count < 2) continue;
            var rep = group.Min(x => x.Id);
            if (!seenRepresentatives.Add(rep)) continue;
            // Only act on groups where every member has an OL key — otherwise
            // the resolver leaves the bare name and there's nothing to do yet.
            if (group.Any(x => string.IsNullOrWhiteSpace(x.OpenLibraryKey))) continue;
            groups.Add(group);
        }

        int authorsRenamed = 0, filesMoved = 0, filesOrphaned = 0;
        var warnings = new List<string>();

        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();

            var sortedByLowestId = group.OrderBy(a => a.Id).ToList();
            var fallback = sortedByLowestId[0];

            // Pre-compute the new folder name for every member.
            var targetByAuthorId = sortedByLowestId.ToDictionary(
                a => a.Id,
                a => AuthorFolderNameResolver.Resolve(a, allAuthors));

            // Build a NormalizedTitle → AuthorId lookup so a file's stored
            // NormalizedTitle picks the right owner. Multiple authors might
            // share a NormalizedTitle (rare); the first wins by lowest id.
            var memberIds = sortedByLowestId.Select(a => a.Id).ToList();
            var books = await db.Books.AsNoTracking()
                .Where(b => memberIds.Contains(b.AuthorId) && b.NormalizedTitle != null)
                .Select(b => new { b.AuthorId, b.NormalizedTitle })
                .ToListAsync(ct);
            var ownerByTitle = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var b in books.OrderBy(x => x.AuthorId))
            {
                var key = b.NormalizedTitle!;
                if (!ownerByTitle.ContainsKey(key)) ownerByTitle[key] = b.AuthorId;
            }

            // Find every LocalBookFile that currently belongs to any group
            // member, no matter which folder leaf the row stores.
            var files = await db.LocalBookFiles
                .Where(f => f.AuthorId != null && memberIds.Contains(f.AuthorId.Value))
                .ToListAsync(ct);

            // Also collect orphan rows whose AuthorFolder matches any candidate
            // folder name for the group — these are pre-merge files that never
            // got an AuthorId assigned.
            var folderCandidates = sortedByLowestId
                .SelectMany(a => new[] { a.Name, a.CalibreFolderName })
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var orphans = await db.LocalBookFiles
                .Where(f => f.AuthorId == null && folderCandidates.Contains(f.AuthorFolder))
                .ToListAsync(ct);
            files.AddRange(orphans);

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                var owner = ResolveOwner(file, ownerByTitle, fallback);
                if (owner.Id == fallback.Id &&
                    (string.IsNullOrEmpty(file.NormalizedTitle) ||
                     !ownerByTitle.ContainsKey(file.NormalizedTitle)))
                {
                    filesOrphaned++;
                }

                var newFolder = targetByAuthorId[owner.Id];
                MoveFileToFolder(file, owner, newFolder, locations, warnings);
                filesMoved++;
            }

            foreach (var member in sortedByLowestId)
            {
                var target = targetByAuthorId[member.Id];
                if (!string.Equals(member.CalibreFolderName, target, StringComparison.Ordinal))
                {
                    member.CalibreFolderName = target;
                    authorsRenamed++;
                }
            }

            await db.SaveChangesAsync(ct);

            // Best-effort: prune the now-empty merged source folder if it still
            // exists under any library root. The new folders live alongside.
            foreach (var libRoot in locations)
                foreach (var legacy in folderCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var path = Path.Combine(libRoot.TrimEnd('\\', '/'), legacy);
                    try
                    {
                        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                            Directory.Delete(path);
                    }
                    catch { /* best effort */ }
                }
        }

        _log.LogInformation(
            "Author folder disambiguator done. Groups={Groups} Renamed={Renamed} Moved={Moved} Orphaned={Orphaned} Warnings={W}",
            groups.Count, authorsRenamed, filesMoved, filesOrphaned, warnings.Count);

        return new AuthorDisambiguationSummary(groups.Count, authorsRenamed, filesMoved, filesOrphaned, warnings);
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
    private void MoveFileToFolder(
        LocalBookFile file,
        Author owner,
        string newFolder,
        IReadOnlyList<string> locations,
        List<string> warnings)
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
            if (destDir is not null) Directory.CreateDirectory(destDir);
            if (File.Exists(file.FullPath))
            {
                var final = UniqueFile(destPath);
                File.Move(file.FullPath, final);
                file.FullPath = final;
            }
            else if (Directory.Exists(file.FullPath))
            {
                var final = UniqueDirectory(Path.GetDirectoryName(destPath)!, Path.GetFileName(destPath));
                Directory.Move(file.FullPath, final);
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

    private static string UniqueFile(string desired)
    {
        if (!File.Exists(desired) && !Directory.Exists(desired)) return desired;
        var dir = Path.GetDirectoryName(desired) ?? "";
        var stem = Path.GetFileNameWithoutExtension(desired);
        var ext  = Path.GetExtension(desired);
        for (var i = 2; i < 1000; i++)
        {
            var next = Path.Combine(dir, $"{stem}_{i}{ext}");
            if (!File.Exists(next) && !Directory.Exists(next)) return next;
        }
        return Path.Combine(dir, $"{stem}_{DateTime.UtcNow:yyyyMMddHHmmss}{ext}");
    }

    private static string UniqueDirectory(string parent, string leaf)
    {
        var candidate = Path.Combine(parent, leaf);
        if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        for (var i = 2; i < 1000; i++)
        {
            var next = Path.Combine(parent, $"{leaf} ({i})");
            if (!Directory.Exists(next) && !File.Exists(next)) return next;
        }
        return Path.Combine(parent, $"{leaf} ({DateTime.UtcNow:yyyyMMddHHmmss})");
    }
}

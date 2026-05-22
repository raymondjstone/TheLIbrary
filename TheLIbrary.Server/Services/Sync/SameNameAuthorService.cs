using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

public sealed record SameNameAuthorSummary(
    int AuthorsScanned,
    int NamesSearched,
    int AuthorsAdded,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Warnings);

// Scheduled job: for every author already on the watchlist, searches
// OpenLibrary for author records with the exact same name and adds any that
// aren't tracked yet. Catches the common case where OpenLibrary has split one
// real author across several author records — the user picks one, this finds
// the rest. New rows go in as Pending; a later works-refresh fills them in.
//
// Runs as a singleton through BackgroundTaskCoordinator so it can't overlap
// with sync, organize, incoming, etc.
public sealed class SameNameAuthorService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly ILogger<SameNameAuthorService> _log;
    private volatile bool _isRunning;
    private SameNameAuthorSummary? _lastResult;

    public SameNameAuthorService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        ILogger<SameNameAuthorService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public SameNameAuthorSummary? LastResult => _lastResult;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("add same-name authors", out var holder))
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
            catch (Exception ex) { _log.LogError(ex, "Same-name author job failed"); }
            finally { _isRunning = false; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    private async Task<SameNameAuthorSummary> RunAsync(CancellationToken ct)
    {
        _log.LogInformation("Same-name author job starting");
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var ol = scope.ServiceProvider.GetRequiredService<OpenLibraryClient>();

        var authors = await db.Authors.AsNoTracking()
            .Select(a => new { a.Name, a.OpenLibraryKey })
            .ToListAsync(ct);

        // Keys already on the watchlist — never re-added.
        var trackedKeys = authors
            .Where(a => !string.IsNullOrEmpty(a.OpenLibraryKey))
            .Select(a => a.OpenLibraryKey!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Names the user explicitly removed/excluded — not re-added.
        var blacklist = (await db.AuthorBlacklist
            .Select(b => b.NormalizedName)
            .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        // One search per distinct normalized name, so linked duplicates and
        // pen-name variants that collapse to the same name aren't searched twice.
        var namesByNorm = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in authors)
        {
            var norm = TitleNormalizer.NormalizeAuthor(a.Name);
            if (norm.Length == 0) continue;
            namesByNorm.TryAdd(norm, a.Name);
        }

        int added = 0;
        var addedNames = new List<string>();
        var warnings = new List<string>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (norm, displayName) in namesByNorm)
        {
            ct.ThrowIfCancellationRequested();
            if (blacklist.Contains(norm)) continue;

            AuthorSearchResponse? resp;
            try
            {
                resp = await ol.SearchAuthorsAsync(displayName, ct);
            }
            catch (Exception ex)
            {
                warnings.Add($"{displayName}: OpenLibrary search failed — {ex.Message}");
                continue;
            }
            if (resp?.Docs is null) continue;

            foreach (var doc in resp.Docs)
            {
                if (string.IsNullOrWhiteSpace(doc.Key) || string.IsNullOrWhiteSpace(doc.Name)) continue;
                // Exact same-name records only — not every fuzzy search hit.
                if (TitleNormalizer.NormalizeAuthor(doc.Name) != norm) continue;

                var key = doc.Key!.StartsWith("/authors/", StringComparison.OrdinalIgnoreCase)
                    ? doc.Key!["/authors/".Length..]
                    : doc.Key!;
                if (trackedKeys.Contains(key)) continue;   // already on the watchlist
                if (!seenKeys.Add(key)) continue;          // already queued this run

                db.Authors.Add(new Author
                {
                    Name = doc.Name!.Trim(),
                    OpenLibraryKey = key,
                    Status = AuthorStatus.Pending,
                });
                added++;
                addedNames.Add($"{doc.Name!.Trim()} ({key})");
            }
        }

        if (added > 0) await db.SaveChangesAsync(ct);

        _log.LogInformation(
            "Same-name author job done. Scanned={Scanned} names, searched {Searched}, added {Added}",
            authors.Count, namesByNorm.Count, added);

        return new SameNameAuthorSummary(authors.Count, namesByNorm.Count, added, addedNames, warnings);
    }
}

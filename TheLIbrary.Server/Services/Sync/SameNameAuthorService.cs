using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

public sealed record SameNameAuthorSummary(
    int AuthorsScanned,
    int NamesMatched,
    int AuthorsAdded,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Warnings);

// Scheduled job: for every author already on the watchlist, finds OpenLibrary
// author records with the exact same name and adds any that aren't tracked
// yet. Catches the common case where OpenLibrary has split one real author
// across several author records — the user picks one, this finds the rest.
//
// Pure DB work: it queries the local OpenLibraryAuthor catalogue (the OL
// authors dump, indexed by NormalizedName) — no OpenLibrary web requests.
// New rows go in as Pending; a later works-refresh fills them in.
//
// Runs as a singleton through BackgroundTaskCoordinator so it can't overlap
// with sync, organize, incoming, etc.
public sealed class SameNameAuthorService
{
    // A name shared by more catalogue records than this is treated as a
    // generic name (not an author OL has split) and skipped, so the watchlist
    // isn't flooded with unrelated people who happen to share a common name.
    private const int MaxAdditionsPerName = 25;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly ILogger<SameNameAuthorService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;
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
    public string? CurrentMessage => _currentMessage;
    public SameNameAuthorSummary? LastResult => _lastResult;

    internal static SameNameAuthorSummary SummarizeForTests(
        IReadOnlyList<(string OlKey, string Name, string NormalizedName)> catalogMatches,
        IReadOnlyCollection<string> trackedKeys,
        IReadOnlyCollection<string> blacklist,
        int authorsCount)
    {
        int added = 0;
        var addedNames = new List<string>();
        var warnings = new List<string>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var trackedNames = catalogMatches.Select(x => x.NormalizedName).Distinct(StringComparer.Ordinal).Count();

        if (catalogMatches.Count == 0)
            warnings.Add("No matches — the OpenLibrary authors catalogue looks empty; run the Seed job.");

        foreach (var group in catalogMatches.GroupBy(o => o.NormalizedName, StringComparer.Ordinal))
        {
            if (blacklist.Contains(group.Key)) continue;

            var newOnes = group
                .Where(o => !string.IsNullOrWhiteSpace(o.OlKey) && !trackedKeys.Contains(o.OlKey))
                .ToList();
            if (newOnes.Count == 0) continue;

            if (newOnes.Count > MaxAdditionsPerName)
            {
                warnings.Add($"'{group.First().Name}': {newOnes.Count} same-name records — skipped as too generic");
                continue;
            }

            foreach (var o in newOnes)
            {
                if (!seenKeys.Add(o.OlKey)) continue;
                added++;
                addedNames.Add($"{o.Name} ({o.OlKey})");
            }
        }

        return new SameNameAuthorSummary(authorsCount, trackedNames, added, addedNames, warnings);
    }

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
            finally { _isRunning = false; _currentMessage = null; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    private async Task<SameNameAuthorSummary> RunAsync(CancellationToken ct)
    {
        _log.LogInformation("Same-name author job starting");
        _currentMessage = "Loading authors";
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

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

        // Distinct normalized names of every tracked author. NormalizeAuthor is
        // exactly what the dump seeder used to fill OpenLibraryAuthor.
        // NormalizedName, so these line up for a direct indexed lookup.
        var trackedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in authors)
        {
            var norm = TitleNormalizer.NormalizeAuthor(a.Name);
            if (norm.Length == 0) continue;
            if (norm.Length > 300) norm = norm[..300];   // catalogue column is nvarchar(300)
            trackedNames.Add(norm);
        }
        if (trackedNames.Count == 0)
            return new SameNameAuthorSummary(authors.Count, 0, 0, Array.Empty<string>(), Array.Empty<string>());

        // One indexed query against the local OL authors catalogue: every
        // catalogue record that shares a normalized name with a tracked author.
        var nameList = trackedNames.ToList();
        _currentMessage = $"Querying catalogue for {trackedNames.Count} name(s)";
        var catalogMatches = await db.OpenLibraryAuthors.AsNoTracking()
            .Where(o => nameList.Contains(o.NormalizedName))
            .Select(o => new { o.OlKey, o.Name, o.NormalizedName })
            .ToListAsync(ct);

        int added = 0;
        var addedNames = new List<string>();
        var warnings = new List<string>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (catalogMatches.Count == 0)
            warnings.Add("No matches — the OpenLibrary authors catalogue looks empty; run the Seed job.");

        foreach (var group in catalogMatches.GroupBy(o => o.NormalizedName, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (blacklist.Contains(group.Key)) continue;

            var newOnes = group
                .Where(o => !string.IsNullOrWhiteSpace(o.OlKey) && !trackedKeys.Contains(o.OlKey))
                .ToList();
            if (newOnes.Count == 0) continue;

            if (newOnes.Count > MaxAdditionsPerName)
            {
                warnings.Add(
                    $"'{group.First().Name}': {newOnes.Count} same-name records — skipped as too generic");
                continue;
            }

            foreach (var o in newOnes)
            {
                if (!seenKeys.Add(o.OlKey)) continue;
                db.Authors.Add(new Author
                {
                    Name = string.IsNullOrWhiteSpace(o.Name) ? o.OlKey : o.Name.Trim(),
                    OpenLibraryKey = o.OlKey,
                    Status = AuthorStatus.Pending,
                    CreationSource = "same-name",
                });
                added++;
                addedNames.Add($"{o.Name} ({o.OlKey})");
            }
        }

        if (added > 0) await db.SaveChangesAsync(ct);

        _log.LogInformation(
            "Same-name author job done. Tracked names={Names}, catalogue matches={Matches}, added {Added}",
            trackedNames.Count, catalogMatches.Count, added);
        _currentMessage = $"Done — added {added} author(s)";

        return new SameNameAuthorSummary(authors.Count, trackedNames.Count, added, addedNames, warnings);
    }
}

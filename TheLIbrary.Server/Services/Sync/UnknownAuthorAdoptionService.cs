using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

public sealed record UnknownAuthorAdoptionSummary(
    int SuffixedFoldersFound,
    int AuthorsAdded,
    int FoldersReturnedToIncoming,
    IReadOnlyList<string> Details,
    IReadOnlyList<string> Warnings);

// Scheduled job: scans the ROOT of the quarantine (__unknown) bucket for
// folders named like the same-name disambiguator's output — "<Author>_OLnnnnnnA".
// For each one it:
//   1. parses out the trailing OpenLibrary author key,
//   2. adds that author to the watchlist from the local OpenLibraryAuthor
//      catalogue (if no Author row already owns that key), and
//   3. moves the folder back to the incoming bucket — stripped of the
//      "_OLkey" suffix so the next incoming run can match it to the author
//      by name and file the books under them.
//
// This recovers collision-suffixed folders that were swept to __unknown (e.g.
// when one of a same-name pair was later removed from the watchlist) without
// any manual clicking. Singleton through BackgroundTaskCoordinator so it can't
// overlap with sync / incoming / organize.
public sealed class UnknownAuthorAdoptionService
{
    // "<name>_OL<digits><letter>" — the disambiguator's folder shape. The OL
    // author key is digits followed by a trailing type letter (A for authors).
    private static readonly Regex OlKeySuffixRx = new(
        @"^(?<name>.+)_(?<key>OL\d+[A-Z])$", RegexOptions.Compiled);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly ILogger<UnknownAuthorAdoptionService> _log;
    private volatile bool _isRunning;
    private UnknownAuthorAdoptionSummary? _lastResult;

    public UnknownAuthorAdoptionService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        ILogger<UnknownAuthorAdoptionService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public UnknownAuthorAdoptionSummary? LastResult => _lastResult;

    // Exposed for unit testing the pure name/key parse.
    internal static (string? Name, string? Key) ParseSuffixedFolder(string folderName)
    {
        var m = OlKeySuffixRx.Match(folderName);
        return m.Success ? (m.Groups["name"].Value, m.Groups["key"].Value) : (null, null);
    }

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("adopt __unknown authors", out var holder))
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
            catch (Exception ex) { _log.LogError(ex, "Adopt-unknown-authors job failed"); }
            finally { _isRunning = false; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    private async Task<UnknownAuthorAdoptionSummary> RunAsync(CancellationToken ct)
    {
        _log.LogInformation("Adopt-unknown-authors job starting");
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

        var details = new List<string>();
        var warnings = new List<string>();

        var incomingSetting = await db.AppSettings
            .FirstOrDefaultAsync(s => s.Key == AppSettingKeys.IncomingFolder, ct);
        var incomingPath = incomingSetting?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(incomingPath))
        {
            warnings.Add("Incoming folder is not configured — nothing moved.");
            return new UnknownAuthorAdoptionSummary(0, 0, 0, details, warnings);
        }
        if (!Directory.Exists(incomingPath))
        {
            warnings.Add($"Incoming folder does not exist: {incomingPath}");
            return new UnknownAuthorAdoptionSummary(0, 0, 0, details, warnings);
        }

        var locations = await db.LibraryLocations
            .Where(l => l.Enabled)
            .Select(l => l.Path)
            .ToListAsync(ct);
        var unknownRoots = await UnknownFolderResolver.GetSourceRootsAsync(db, locations, ct);

        // OL keys already on the watchlist — never re-added.
        var trackedKeys = (await db.Authors
                .Where(a => a.OpenLibraryKey != null && a.OpenLibraryKey != "")
                .Select(a => a.OpenLibraryKey!)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Names the user explicitly excluded — don't resurrect them.
        var blacklist = (await db.AuthorBlacklist
                .Select(b => b.NormalizedName)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        int suffixedFound = 0, authorsAdded = 0, foldersReturned = 0;

        foreach (var unknownRoot in unknownRoots)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(unknownRoot)) continue;

            foreach (var dir in Directory.GetDirectories(unknownRoot))
            {
                ct.ThrowIfCancellationRequested();
                var folderName = Path.GetFileName(dir);
                var (namePart, olKey) = ParseSuffixedFolder(folderName);
                if (namePart is null || olKey is null) continue;   // not a "_OLkey" folder
                suffixedFound++;

                // 1. Add the author from the OL catalogue if no row owns the key.
                if (!trackedKeys.Contains(olKey))
                {
                    var olRow = await db.OpenLibraryAuthors
                        .AsNoTracking()
                        .FirstOrDefaultAsync(o => o.OlKey == olKey, ct);
                    var name = !string.IsNullOrWhiteSpace(olRow?.Name) ? olRow!.Name.Trim() : namePart.Trim();
                    var norm = TitleNormalizer.NormalizeAuthor(name);

                    if (!string.IsNullOrEmpty(norm) && blacklist.Contains(norm))
                    {
                        warnings.Add($"{folderName}: '{name}' is blacklisted — left in __unknown");
                        continue;   // don't move blacklisted authors back to incoming
                    }

                    db.Authors.Add(new Author
                    {
                        Name = name,
                        OpenLibraryKey = olKey,
                        Status = AuthorStatus.Pending,
                    });
                    await db.SaveChangesAsync(ct);
                    trackedKeys.Add(olKey);
                    authorsAdded++;
                    details.Add($"Added {name} ({olKey})"
                        + (olRow is null ? " [not in OL catalogue — used folder name]" : ""));
                }

                // 2. Move the folder back to incoming, stripped of the suffix so
                //    incoming can match it to the author by name.
                var destLeaf = SanitizeLeaf(namePart);
                var dest = UniqueDirectory(incomingPath, destLeaf);
                try
                {
                    MoveDirectory(dir, dest);
                    foldersReturned++;
                    details.Add($"Returned '{folderName}' → incoming/{Path.GetFileName(dest)}");
                }
                catch (Exception ex)
                {
                    warnings.Add($"{folderName}: could not move to incoming — {ex.Message}");
                }
            }
        }

        var summary = new UnknownAuthorAdoptionSummary(
            suffixedFound, authorsAdded, foldersReturned, details, warnings);
        _log.LogInformation(
            "Adopt-unknown-authors job done. Suffixed folders={Found} Added={Added} Returned={Returned}",
            suffixedFound, authorsAdded, foldersReturned);
        return summary;
    }

    // Cross-volume-safe directory move. incoming and __unknown are frequently
    // separate mounts (Docker/Azure), where Directory.Move throws EXDEV — fall
    // back to a recursive copy + delete in that case.
    private static void MoveDirectory(string src, string dest)
    {
        try
        {
            Directory.Move(src, dest);
            return;
        }
        catch (IOException)
        {
            // Cross-device (or dest on another volume) — copy then delete.
        }
        CopyDirectory(src, dest);
        Directory.Delete(src, recursive: true);
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var sub in Directory.GetDirectories(src))
            CopyDirectory(sub, Path.Combine(dest, Path.GetFileName(sub)));
    }

    private static string UniqueDirectory(string parent, string preferredLeaf)
    {
        var dest = Path.Combine(parent, preferredLeaf);
        if (!Directory.Exists(dest)) return dest;
        for (int n = 1; ; n++)
        {
            dest = Path.Combine(parent, $"{preferredLeaf}_{n}");
            if (!Directory.Exists(dest)) return dest;
        }
    }

    private static string SanitizeLeaf(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Unknown" : cleaned;
    }
}

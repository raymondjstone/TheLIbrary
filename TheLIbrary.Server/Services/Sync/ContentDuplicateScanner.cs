using System.Security.Cryptography;

namespace TheLibrary.Server.Services.Sync;

public sealed record DuplicateScanResult(
    int FilesScanned,
    int FilesHashed,
    int HashFailures,
    int DuplicateGroups,
    int FilesDeleted,
    int EmptyFilesDeleted,
    long BytesFreed,
    int NearDuplicates,
    IReadOnlyList<string> DeletedPaths);

// The single, shared definition of "byte-identical duplicate" used by both the
// __unknown quarantine dedupe and the per-author-folder dedupe. Given a set of
// candidate file paths it:
//   * deletes zero-byte files outright (an empty ebook is junk, not a copy),
//   * groups the rest by size (cheap — no reads), and only same-size groups are
//     read and SHA-256-hashed,
//   * within each identical-content group keeps ONE copy and deletes the rest.
// The kept copy is the one in `preferKeep` if any (so a matched book never loses
// its only file), then the shortest full path (un-suffixed originals beat
// "_1"-style collision copies), then alphabetical — deterministic either way.
// Returns the deleted paths so the caller can prune the matching DB rows.
public static class ContentDuplicateScanner
{
    public static async Task<DuplicateScanResult> ScanAndDeleteAsync(
        IEnumerable<string> candidatePaths,
        ILogger log,
        Action<string>? progress,
        CancellationToken ct,
        ISet<string>? preferKeep = null)
    {
        var bySize = new Dictionary<long, List<string>>();
        var deletedPaths = new List<string>();
        int filesScanned = 0, emptyDeleted = 0;

        // Pass 1: stat only. A unique size can't have a byte-identical twin, so
        // it's never read. Zero-byte files are deleted on sight.
        foreach (var path in candidatePaths)
        {
            ct.ThrowIfCancellationRequested();
            filesScanned++;
            if (filesScanned % 1000 == 0) progress?.Invoke($"Scanned {filesScanned} file(s)");

            long size;
            try { size = new FileInfo(path).Length; }
            catch (Exception ex) { log.LogWarning(ex, "Dedupe: could not stat {Path}", path); continue; }

            if (size == 0)
            {
                try
                {
                    File.Delete(path);
                    deletedPaths.Add(path);
                    emptyDeleted++;
                    log.LogInformation("Dedupe: deleted zero-byte file {Path}", path);
                }
                catch (Exception ex) { log.LogWarning(ex, "Dedupe: could not delete zero-byte file {Path}", path); }
                continue;
            }
            if (!bySize.TryGetValue(size, out var list)) bySize[size] = list = new List<string>();
            list.Add(path);
        }

        // Pass 2: hash same-size groups, split by content, delete all but one.
        int filesHashed = 0, hashFailures = 0, duplicateGroups = 0, filesDeleted = 0;
        int lookalikes = 0, lookalikesLogged = 0;
        long bytesFreed = 0;
        foreach (var (size, paths) in bySize.Where(kv => kv.Value.Count > 1))
        {
            ct.ThrowIfCancellationRequested();

            var byHash = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                filesHashed++;
                progress?.Invoke($"Hashing candidate {filesHashed}: {Path.GetFileName(path)}");
                string hash;
                try
                {
                    await using var stream = File.OpenRead(path);
                    hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
                }
                catch (Exception ex)
                {
                    hashFailures++;
                    log.LogWarning(ex, "Dedupe: could not hash {Path}", path);
                    continue;
                }
                if (!byHash.TryGetValue(hash, out var list)) byHash[hash] = list = new List<string>();
                list.Add(path);
            }

            // Diagnostic: same name + size but different bytes is the classic
            // "looks like a duplicate but isn't" case (a re-download repackages
            // the zip). Surface a sample so a zero-deletion run is explainable.
            if (byHash.Count > 1)
            {
                var sameNameDifferentBytes = byHash
                    .SelectMany(kv => kv.Value, (kv, path) => (Hash: kv.Key, Path: path))
                    .GroupBy(x => Path.GetFileName(x.Path), StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Select(x => x.Hash).Distinct(StringComparer.Ordinal).Count() > 1);
                foreach (var g in sameNameDifferentBytes)
                {
                    lookalikes++;
                    if (lookalikesLogged < 20)
                    {
                        lookalikesLogged++;
                        log.LogInformation(
                            "Dedupe: same name and size but different contents — NOT byte-identical, kept: {Paths}",
                            string.Join(" | ", g.Select(x => x.Path)));
                    }
                }
            }

            foreach (var group in byHash.Values.Where(g => g.Count > 1))
            {
                duplicateGroups++;
                var keep = ChooseKeeper(group, preferKeep);
                foreach (var dup in group.Where(p => !string.Equals(p, keep, StringComparison.OrdinalIgnoreCase)))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        File.Delete(dup);
                        deletedPaths.Add(dup);
                        filesDeleted++;
                        bytesFreed += size;
                        log.LogInformation("Dedupe: deleted {Dup} (identical to {Keep})", dup, keep);
                    }
                    catch (Exception ex) { log.LogWarning(ex, "Dedupe: could not delete {Path}", dup); }
                }
            }
        }

        return new DuplicateScanResult(
            filesScanned, filesHashed, hashFailures, duplicateGroups,
            filesDeleted, emptyDeleted, bytesFreed, lookalikes, deletedPaths);
    }

    internal static string ChooseKeeper(IReadOnlyList<string> group, ISet<string>? preferKeep = null)
        => group
            .OrderByDescending(p => preferKeep is not null && preferKeep.Contains(p) ? 1 : 0)
            .ThenBy(p => p.Length)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .First();
}

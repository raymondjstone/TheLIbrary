namespace TheLibrary.Server.Services.Calibre;

public sealed record CalibreBookEntry(
    string LocationPath,
    string AuthorFolder,
    string TitleFolder,
    string FullPath,
    long SizeBytes,
    DateTime ModifiedAt);

public sealed class CalibreScanner
{
    // Quarantine bucket for author folders that can't be resolved on
    // OpenLibrary. Files moved under this folder are invisible to the app.
    public const string UnknownAuthorFolder = "__unknown";

    private readonly ILogger<CalibreScanner> _log;

    public CalibreScanner(ILogger<CalibreScanner> log) { _log = log; }

    public IReadOnlyList<CalibreBookEntry> Scan(IEnumerable<string> roots, IEnumerable<string>? ignoredFolders = null)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { UnknownAuthorFolder };
        if (ignoredFolders is not null)
            foreach (var name in ignoredFolders)
                if (!string.IsNullOrWhiteSpace(name)) ignored.Add(name.Trim());

        var results = new List<CalibreBookEntry>();
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            if (!Directory.Exists(root))
            {
                _log.LogWarning("Library location not found: {Root}", root);
                continue;
            }

            foreach (var authorDir in EnumerateDirsSafe(root))
            {
                var authorFolder = Path.GetFileName(authorDir)!;
                if (ignored.Contains(authorFolder)) continue;
                var bookDirs = EnumerateDirsSafe(authorDir).ToList();
                if (bookDirs.Count == 0)
                {
                    var (s, m) = Fingerprint(authorDir);
                    results.Add(new CalibreBookEntry(root, authorFolder, "", authorDir, s, m));
                    continue;
                }
                foreach (var bookDir in bookDirs)
                {
                    var titleFolder = Path.GetFileName(bookDir)!;
                    var (size, mtime) = Fingerprint(bookDir);
                    results.Add(new CalibreBookEntry(root, authorFolder, titleFolder, bookDir, size, mtime));
                }
            }
        }
        return results;
    }

    private IEnumerable<string> EnumerateDirsSafe(string path)
    {
        try { return Directory.EnumerateDirectories(path); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Unable to enumerate {Path}", path);
            return Array.Empty<string>();
        }
    }

    // Summed size + max LastWriteTime of files directly inside the folder.
    // Cheap — only uses FileSystem metadata. Good enough to detect content
    // changes without hashing gigabytes of ebooks every sync.
    private (long size, DateTime mtime) Fingerprint(string dir)
    {
        long size = 0;
        var mtime = DateTime.MinValue;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var info = new FileInfo(file);
                size += info.Length;
                if (info.LastWriteTimeUtc > mtime) mtime = info.LastWriteTimeUtc;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Unable to fingerprint {Path}", dir);
        }
        return (size, mtime);
    }
}

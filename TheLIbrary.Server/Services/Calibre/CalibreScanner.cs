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

    // Ebook file extensions recognised as book content (not covers or metadata).
    // .txt is included for Project Gutenberg-style plain-text books — they
    // have no metadata so the filename fallback ("Author - Title.txt") is the
    // only signal, but they preview natively in the browser like EPUB/PDF.
    internal static readonly HashSet<string> EbookExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".mobi", ".azw", ".azw3", ".azw4", ".kf8", ".prc", ".pdb",
        ".fb2", ".fbz", ".pdf", ".lit", ".cbz", ".docx", ".odt", ".txt"
    };

    // Archive extensions handled by the unzip job (kept, not deleted as junk).
    internal static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar"
    };

    // File extensions that are definitively not books or archives and should be
    // deleted on sight. Common culprits from Calibre imports and Windows drag-drops.
    internal static readonly HashSet<string> JunkExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xml", ".inf", ".nfo", ".db", ".ini", ".url", ".lnk",
        ".tmp", ".bak", ".log", ".exe", ".bat", ".cmd", ".js",
        ".html", ".htm", ".css", ".part", ".crdownload"
    };

    private readonly ILogger<CalibreScanner> _log;

    public CalibreScanner(ILogger<CalibreScanner> log) { _log = log; }

    // Scans library roots and returns one entry per discovered book location.
    //
    // Supported layouts (both handled transparently):
    //   Classic Calibre:   root\Author\Title Folder\book.epub
    //   Flat-file (new):   root\Author\Series\book.epub   or   root\Author\book.epub
    //
    // In the flat-file layout each ebook file is its own CalibreBookEntry with
    // FullPath pointing to the file (not a folder). In the classic layout FullPath
    // points to the title folder.
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

                bool addedAnyEntry = false;

                // Standalone ebook files sitting directly in the author folder
                // (flat-file layout: no series subfolder, no title subfolder).
                foreach (var filePath in EnumerateEbookFilesSafe(authorDir))
                {
                    var (size, mtime) = FingerprintFile(filePath);
                    results.Add(new CalibreBookEntry(root, authorFolder,
                        Path.GetFileNameWithoutExtension(filePath)!, filePath, size, mtime));
                    addedAnyEntry = true;
                }

                var bookDirs = EnumerateDirsSafe(authorDir).ToList();
                if (bookDirs.Count == 0)
                {
                    if (!addedAnyEntry)
                    {
                        // Truly empty author folder — record the state so sync tracking
                        // can notice the folder and the author is not silently dropped.
                        var (s, m) = Fingerprint(authorDir);
                        results.Add(new CalibreBookEntry(root, authorFolder, "", authorDir, s, m));
                    }
                    continue;
                }

                foreach (var bookDir in bookDirs)
                {
                    var ebookFiles = EnumerateEbookFilesSafe(bookDir).ToList();
                    if (ebookFiles.Count > 0)
                    {
                        // Flat-file layout: this subdir directly contains ebook files.
                        // Each file is its own entry (series container or single-book dir).
                        foreach (var filePath in ebookFiles)
                        {
                            var (size, mtime) = FingerprintFile(filePath);
                            results.Add(new CalibreBookEntry(root, authorFolder,
                                Path.GetFileNameWithoutExtension(filePath)!, filePath, size, mtime));
                        }
                    }
                    else
                    {
                        var subDirs = EnumerateDirsSafe(bookDir).ToList();
                        if (subDirs.Count > 0 && !HasFiles(bookDir))
                        {
                            // Classic Calibre: series folder whose children are title folders.
                            foreach (var titleDir in subDirs)
                            {
                                var titleFolder = Path.GetFileName(titleDir)!;
                                var (size, mtime) = Fingerprint(titleDir);
                                results.Add(new CalibreBookEntry(root, authorFolder, titleFolder, titleDir, size, mtime));
                            }
                        }
                        else
                        {
                            // Classic Calibre title folder (no ebook files — metadata-only
                            // or empty after files were moved by the organizer).
                            var titleFolder = Path.GetFileName(bookDir)!;
                            var (size, mtime) = Fingerprint(bookDir);
                            results.Add(new CalibreBookEntry(root, authorFolder, titleFolder, bookDir, size, mtime));
                        }
                    }
                }
            }
        }
        return results;
    }

    private IEnumerable<string> EnumerateEbookFilesSafe(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path)
                .Where(f => EbookExtensions.Contains(Path.GetExtension(f))
                         || ArchiveExtensions.Contains(Path.GetExtension(f)));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Unable to enumerate files in {Path}", path);
            return Array.Empty<string>();
        }
    }

    private bool HasFiles(string path)
    {
        try { return Directory.EnumerateFiles(path).Any(); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Unable to check files in {Path}", path);
            return false;
        }
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

    // For a directory: summed size + max mtime of files directly inside.
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

    // For a single file: its own size and mtime.
    private static (long size, DateTime mtime) FingerprintFile(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            return (info.Length, info.LastWriteTimeUtc);
        }
        catch
        {
            return (0, DateTime.MinValue);
        }
    }
}

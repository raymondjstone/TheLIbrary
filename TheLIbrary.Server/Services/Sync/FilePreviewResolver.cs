namespace TheLibrary.Server.Services.Sync;

// Pure-function "which physical file on disk does this preview request want"
// helper. Splitting it out keeps the path-traversal guard easy to unit-test
// without spinning up a controller or filesystem.
public static class FilePreviewResolver
{
    // Formats the browser can render natively (with epub.js for EPUB) and the
    // Content-Type to stream them with. Anything outside this set is rejected
    // before any disk I/O so we never accidentally serve random binaries.
    public static readonly IReadOnlyDictionary<string, string> SupportedFormats =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["epub"] = "application/epub+zip",
            ["pdf"]  = "application/pdf",
            ["txt"]  = "text/plain; charset=utf-8",
            ["cbz"]  = "application/x-cbz",
            ["zip"]  = "application/zip",
        };

    public sealed record Resolution(string FullPath, string ContentType, string FileName);

    public enum FailureKind { UnsupportedFormat, NoMatchingFile, OutsideLibrary }

    public sealed record ResolutionResult(Resolution? Ok, FailureKind? Failure);

    // Resolves a preview request against the on-disk layout. `storedPath` is
    // either a file path (flat-file layout) or a directory path (classic
    // Calibre). `format` is the lower-cased extension the user requested. The
    // returned path is guaranteed to be:
    //   - canonicalised (no `..`)
    //   - of a supported extension
    //   - inside one of `libraryRoots`
    public static ResolutionResult Resolve(
        string storedPath,
        string format,
        IReadOnlyList<string> libraryRoots,
        Func<string, IEnumerable<string>>? enumerateFiles = null)
    {
        if (!SupportedFormats.TryGetValue(format, out var contentType))
            return new ResolutionResult(null, FailureKind.UnsupportedFormat);

        if (string.IsNullOrWhiteSpace(storedPath))
            return new ResolutionResult(null, FailureKind.NoMatchingFile);

        // Decide which actual file to serve.
        string? candidate = null;
        var storedExt = Path.GetExtension(storedPath).TrimStart('.').ToLowerInvariant();
        if (string.Equals(storedExt, format, StringComparison.OrdinalIgnoreCase))
        {
            candidate = storedPath;
        }
        else
        {
            // Directory layout (classic Calibre): pick the first file in the
            // directory whose extension matches the requested format. The
            // enumerator is parameterised so tests can avoid disk I/O.
            enumerateFiles ??= Directory.EnumerateFiles;
            try
            {
                candidate = enumerateFiles(storedPath)
                    .FirstOrDefault(f => string.Equals(
                        Path.GetExtension(f).TrimStart('.'), format,
                        StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                candidate = null;
            }
        }
        if (candidate is null) return new ResolutionResult(null, FailureKind.NoMatchingFile);

        // Canonicalise + library-root check. After this we know the file is:
        //   - real (Path.GetFullPath normalises `..` and `.`)
        //   - inside an enabled location (defence in depth — the FullPath in the
        //     DB SHOULD already be inside one, but a tampered row shouldn't
        //     reveal random files)
        var canonical = Path.GetFullPath(candidate);
        if (!IsInsideAnyRoot(canonical, libraryRoots))
            return new ResolutionResult(null, FailureKind.OutsideLibrary);

        return new ResolutionResult(
            new Resolution(canonical, contentType, Path.GetFileName(canonical)),
            null);
    }

    // True when `path` lives under at least one `roots` entry after both have
    // been canonicalised. Compares with an OS-appropriate trailing separator
    // so `/Books/Coll` doesn't match `/Books/Collection`.
    public static bool IsInsideAnyRoot(string path, IReadOnlyList<string> roots)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var canonicalPath = Path.GetFullPath(path);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var canonicalRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var withSep = canonicalRoot + Path.DirectorySeparatorChar;
            if (canonicalPath.StartsWith(withSep, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(canonicalPath, canonicalRoot, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

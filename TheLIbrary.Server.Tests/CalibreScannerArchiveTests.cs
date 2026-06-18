using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

// The scanner must skip the archive folder by absolute path (not just by top-level
// name) so a nested or relocated archive is still excluded — archived files must
// never be re-indexed as live library content.
public class CalibreScannerArchiveTests : IDisposable
{
    private readonly string _root;

    public CalibreScannerArchiveTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lib-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string relativeFile)
    {
        var full = Path.Combine(_root, relativeFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "x");
    }

    [Fact]
    public void Scan_Excludes_Archive_Folder_Nested_Below_Root_Level()
    {
        // A live book and an archived copy nested two levels under the root
        // (root/__archive/<author>/<title>/file) — the archived one must not appear.
        Write("Greer Vance Mallory/A Tide of Ashes.epub");
        Write("__archive/Greer Vance Mallory/A Tide of Ashes/A Tide of Ashes.epub");

        var scanner = new CalibreScanner(NullLogger<CalibreScanner>.Instance);

        // Name-based ignore of "__archive" already covers the direct-child case, so
        // prove the PATH-based exclusion by NOT passing the name — only the path.
        var entries = scanner.Scan(
            new[] { _root },
            ignoredFolders: null,
            ignoredPaths: ArchivePolicy.AbsoluteDirs("__archive", new[] { _root }));

        Assert.Contains(entries, e => e.FullPath.Replace('\\', '/').Contains("/Greer Vance Mallory/A Tide of Ashes.epub"));
        Assert.DoesNotContain(entries, e => e.FullPath.Replace('\\', '/').Contains("/__archive/"));
    }

    [Fact]
    public void Scan_Excludes_Absolute_Archive_Path()
    {
        Write("Greer Vance Mallory/A Tide of Ashes.epub");
        Write("TheLibrary_Archive/Greer Vance Mallory/A Tide of Ashes/A Tide of Ashes.epub");

        var absoluteArchive = Path.Combine(_root, "TheLibrary_Archive").Replace('\\', '/');
        var scanner = new CalibreScanner(NullLogger<CalibreScanner>.Instance);

        var entries = scanner.Scan(
            new[] { _root },
            ignoredFolders: null,
            ignoredPaths: ArchivePolicy.AbsoluteDirs(absoluteArchive, new[] { _root }));

        Assert.Contains(entries, e => e.FullPath.Replace('\\', '/').Contains("/Greer Vance Mallory/A Tide of Ashes.epub"));
        Assert.DoesNotContain(entries, e => e.FullPath.Replace('\\', '/').Contains("/TheLibrary_Archive/"));
    }
}

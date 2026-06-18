using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class SyncServiceCleanupTests
{
    [Fact]
    public void ComputeCleanupSetsForTests_Computes_Removed_StaleOrphan_And_DefinitelyStale_Sets()
    {
        var existing = new List<LocalBookFile>
        {
            new() { Id = 1, FullPath = "C:\\lib\\Author\\gone.epub", AuthorFolder = "Author", AuthorId = 1 },
            new() { Id = 2, FullPath = "C:\\lib\\Unknown\\orphan.epub", AuthorFolder = "Unknown", AuthorId = null },
            new() { Id = 3, FullPath = "C:\\lib\\Author\\folder", AuthorFolder = "Author", AuthorId = 1 }
        };
        var existingByPath = existing.ToDictionary(
            x => x.FullPath.Normalize(System.Text.NormalizationForm.FormC).ToUpperInvariant(),
            x => x,
            StringComparer.Ordinal);
        var deduped = new Dictionary<string, CalibreBookEntry>(StringComparer.Ordinal)
        {
            ["C:\\LIB\\AUTHOR\\LIVE.EPUB"] = new("C:\\lib", "Author", "live", "C:\\lib\\Author\\live.epub", 1, DateTime.UtcNow)
        };

        var result = SyncService.ComputeCleanupSetsForTests(
            existing,
            existingByPath,
            deduped,
            updatedIds: [],
            trackedFolderKeys: [TitleNormalizer.NormalizeAuthor("Author")],
            fileExists: _ => false);

        Assert.Contains(1, result.RemovedIds);
        Assert.Contains(2, result.StaleOrphanIds);
        Assert.Contains(1, result.DefinitelyStaleIds);
        Assert.Contains(3, result.DefinitelyStaleIds);
    }

    [Fact]
    public void ComputeCleanupSetsForTests_Excludes_Updated_Ids_From_Removed_Set()
    {
        var row = new LocalBookFile { Id = 5, FullPath = "C:\\lib\\Author\\book.epub", AuthorFolder = "Author", AuthorId = 1 };
        var existing = new List<LocalBookFile> { row };
        var existingByPath = new Dictionary<string, LocalBookFile>(StringComparer.Ordinal)
        {
            [row.FullPath.Normalize(System.Text.NormalizationForm.FormC).ToUpperInvariant()] = row
        };

        var result = SyncService.ComputeCleanupSetsForTests(
            existing,
            existingByPath,
            new Dictionary<string, CalibreBookEntry>(StringComparer.Ordinal),
            updatedIds: [5],
            trackedFolderKeys: [TitleNormalizer.NormalizeAuthor("Author")],
            fileExists: _ => true);

        Assert.DoesNotContain(5, result.RemovedIds);
    }

    [Fact]
    public void ComputeCleanupSetsForTests_Preserves_Archived_Rows()
    {
        // An archived copy keeps its LocalBookFile row but the scan no longer
        // descends into the archive folder, so its path is absent from `deduped`.
        // It must NOT be deleted by any cleanup pass — archived files are inert and
        // the Archived Files page reads these very rows.
        var existing = new List<LocalBookFile>
        {
            new() { Id = 1, FullPath = "/Books/__archive/Gregor Vance Mallory/A Tide of Ashes.epub", AuthorFolder = "Gregor Vance Mallory", AuthorId = 1 },
            new() { Id = 2, FullPath = "/Books/__archive/Loose/orphan.epub", AuthorFolder = "Loose", AuthorId = null },
            new() { Id = 3, FullPath = "/Books/__archive/Gregor Vance Mallory/folder", AuthorFolder = "Gregor Vance Mallory", AuthorId = 1 },
        };
        var existingByPath = existing.ToDictionary(
            x => x.FullPath.Normalize(System.Text.NormalizationForm.FormC).ToUpperInvariant(),
            x => x,
            StringComparer.Ordinal);

        var result = SyncService.ComputeCleanupSetsForTests(
            existing,
            existingByPath,
            new Dictionary<string, CalibreBookEntry>(StringComparer.Ordinal), // scan skipped the archive
            updatedIds: [],
            trackedFolderKeys: [],
            fileExists: _ => false,
            archiveLeaf: "__archive");

        Assert.Empty(result.RemovedIds);
        Assert.Empty(result.StaleOrphanIds);
        Assert.Empty(result.DefinitelyStaleIds);
    }
}

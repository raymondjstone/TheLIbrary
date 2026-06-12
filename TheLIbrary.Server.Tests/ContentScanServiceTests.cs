using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.Scheduling;
using TheLibrary.Server.Services.Sync;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

public class ContentScanServiceTests
{
    [Fact]
    public async Task Scans_Unmatched_File_Stores_Determination_And_Only_Once()
    {
        var fs = new FakeFileSystem();
        // An EPUB whose front matter carries a Gutenberg-style header.
        var epub = TestEpub.Build(("c.xhtml",
            "<html><body><p>Title: The Ship Who Sang</p><p>Author: Anne McCaffrey</p>" +
            "<p>Copyright 1969 by Anne McCaffrey</p></body></html>"));
        fs.AddFile("/lib/A/orphan.epub", epub);

        var dbName = NewDb();
        await using (var db = CreateDb(dbName))
        {
            db.LibraryLocations.Add(new LibraryLocation { Path = "/lib", Enabled = true });
            db.LocalBookFiles.Add(new LocalBookFile
            {
                FullPath = "/lib/A/orphan.epub",
                SizeBytes = 1,
                Author = new Author { Name = "Folder Author" }, // AuthorId set, BookId null (unmatched)
            });
            await db.SaveChangesAsync();
        }

        var summary = await CreateService(fs, dbName).RunForTestsAsync(CancellationToken.None);
        Assert.Equal(1, summary.Scanned);
        Assert.Equal(1, summary.WithInfo);

        await using (var verify = CreateDb(dbName))
        {
            var scan = await verify.BookContentScans.SingleAsync();
            Assert.Equal("The Ship Who Sang", scan.Title);
            Assert.Equal("Anne McCaffrey", scan.Author);
            Assert.Equal("unmatched", scan.Source);
        }

        // Second run scans nothing — the file was already recorded.
        var second = await CreateService(fs, dbName).RunForTestsAsync(CancellationToken.None);
        Assert.Equal(0, second.Scanned);
    }

    [Fact]
    public async Task ScanAuthor_Caps_Long_AlsoBy_List_And_Reports_Result()
    {
        var fs = new FakeFileSystem();
        var manyTitles = string.Concat(Enumerable.Range(0, 40)
            .Select(i => $"<p>Some Rather Long Book Title Number {i} That Keeps Going And Going</p>"));
        var epub = TestEpub.Build(("c.xhtml", "<html><body><p>Also by Anne McCaffrey</p>" + manyTitles + "</body></html>"));
        fs.AddFile("/lib/A/x.epub", epub);

        var dbName = NewDb();
        await using (var db = CreateDb(dbName))
        {
            db.LibraryLocations.Add(new LibraryLocation { Path = "/lib", Enabled = true });
            db.LocalBookFiles.Add(new LocalBookFile
            {
                FullPath = "/lib/A/x.epub", SizeBytes = 1,
                Author = new Author { Id = 1, Name = "Anne McCaffrey" },
            });
            await db.SaveChangesAsync();
        }

        var res = await CreateService(fs, dbName).ScanAuthorAsync(1, CancellationToken.None);

        Assert.Equal(1, res.Scanned);
        Assert.Equal(0, res.Errors);
        Assert.Equal(0, res.Remaining);
        await using var verify = CreateDb(dbName);
        var scan = await verify.BookContentScans.SingleAsync();
        Assert.NotNull(scan.AlsoByTitles);
        Assert.True(scan.AlsoByTitles!.Length <= 2000, $"AlsoByTitles was {scan.AlsoByTitles.Length} chars"); // clamped to the column length
    }

    [Fact]
    public async Task Untracked_Scan_Prefers_Validated_Embedded_Metadata_Over_Prose_And_Filename()
    {
        // The quarantine pattern this guards: filename title truncated to ~30
        // chars, author in "Last, First" — while the OPF carries the full title
        // and a clean author. FileMetadataReader reads the REAL filesystem, so
        // the epub goes on disk too (BookTextReader reads via the fake).
        var dir = Path.Combine(Path.GetTempPath(), $"contentscan-meta-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "The Full Untruncated Stor - Fenwick, Quigley.epub");
        var epub = TestEpub.BuildWithMetadata(
            "The Full Untruncated Story Of Everything",
            "Fenwick, Quigley",
            ("c.xhtml", "<html><body><p>plain prose with no author signal at all</p></body></html>"));
        try
        {
            await File.WriteAllBytesAsync(path, epub);
            var fs = new FakeFileSystem();
            fs.AddFile(path, epub);

            var dbName = NewDb();
            await using (var db = CreateDb(dbName))
            {
                db.LibraryLocations.Add(new LibraryLocation { Path = dir, Enabled = true });
                db.UnknownFiles.Add(new UnknownFile
                {
                    FullPath = path,
                    FileName = Path.GetFileName(path),
                    SizeBytes = 1,
                    ModifiedAt = DateTime.UtcNow,
                    ScannedAt = DateTime.UtcNow,
                });
                db.OpenLibraryAuthors.Add(new OpenLibraryAuthor
                {
                    OlKey = "OL77A",
                    Name = "Quigley Fenwick",
                    NormalizedName = TitleNormalizer.NormalizeAuthor("Quigley Fenwick"),
                    ImportedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }

            var summary = await CreateService(fs, dbName).RunForTestsAsync(CancellationToken.None);
            Assert.Equal(1, summary.Scanned);
            Assert.Equal(1, summary.WithInfo);

            await using var verify = CreateDb(dbName);
            var scan = await verify.BookContentScans.SingleAsync();
            Assert.Equal("Quigley Fenwick", scan.Author); // validated, display orientation
            Assert.Equal("The Full Untruncated Story Of Everything", scan.Title);
            // The validated guess also pre-provisions the Pending author.
            Assert.Contains(await verify.Authors.ToListAsync(),
                a => a.OpenLibraryKey == "OL77A" && a.Status == AuthorStatus.Pending);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Enrich_Fills_Missing_Title_From_Filename_That_Agrees_With_Author_Guess()
    {
        // The stuck-row shape: content gave an author but no title, so the
        // assigner's OL work search could never run. The filename has it.
        var dbName = NewDb();
        await using var db = CreateDb(dbName);
        db.BookContentScans.Add(new BookContentScan
        {
            Id = 1,
            FullPath = "/Books/TheLibrary_Unknown/Through Mist - Parker Jaywick.azw3",
            Source = "untracked",
            Author = "Parker Jaywick",
            ScannedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var enriched = await CreateService(new FakeFileSystem(), dbName)
            .EnrichUntrackedFromFilenamesAsync(db, CancellationToken.None);

        Assert.Equal(1, enriched);
        var row = await db.BookContentScans.SingleAsync();
        Assert.Equal("Through Mist", row.Title);
        Assert.Equal("Parker Jaywick", row.Author); // author untouched
    }

    [Fact]
    public async Task Prune_Removes_Unreviewed_Rows_Whose_Path_No_Index_Claims()
    {
        var dbName = NewDb();
        await using var db = CreateDb(dbName);
        // Stale: nothing claims this path any more (file was moved/deleted).
        db.BookContentScans.Add(new BookContentScan
        { Id = 1, FullPath = "/lib/__unknown/gone.epub", Source = "untracked", Author = "X", ScannedAt = DateTime.UtcNow });
        // Live untracked: still in the UnknownFiles index.
        db.BookContentScans.Add(new BookContentScan
        { Id = 2, FullPath = "/lib/__unknown/here.epub", Source = "untracked", Author = "X", ScannedAt = DateTime.UtcNow });
        db.UnknownFiles.Add(new UnknownFile { Id = 1, FullPath = "/lib/__unknown/here.epub", FileName = "here.epub" });
        // Live unmatched: still a tracked LocalBookFile.
        db.BookContentScans.Add(new BookContentScan
        { Id = 3, FullPath = "/lib/A/file.epub", Source = "unmatched", Author = "X", ScannedAt = DateTime.UtcNow });
        db.LocalBookFiles.Add(new LocalBookFile { Id = 1, FullPath = "/lib/A/file.epub" });
        // Stale but REVIEWED with a catalogue — must survive (feeds apply-catalog).
        db.BookContentScans.Add(new BookContentScan
        { Id = 4, FullPath = "/lib/B/moved-away.epub", Source = "unmatched", Reviewed = true, SeriesCatalogJson = "[]", ScannedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var pruned = await CreateService(new FakeFileSystem(), dbName)
            .PruneStaleRowsAsync(db, CancellationToken.None);

        Assert.Equal(1, pruned);
        var remaining = await db.BookContentScans.Select(c => c.Id).OrderBy(x => x).ToListAsync();
        Assert.Equal(new[] { 2, 3, 4 }, remaining);
    }

    private static string NewDb() => $"contentscan-tests-{System.Guid.NewGuid():N}";
    private static LibraryDbContext CreateDb(string name)
        => new(new DbContextOptionsBuilder<LibraryDbContext>().UseInMemoryDatabase(name).Options);

    private static ContentScanService CreateService(FakeFileSystem fs, string dbName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<LibraryDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();
        var converter = new CalibreConverter(
            Options.Create(new CalibreOptions()), fs, new NoopRunner(), NullLogger<CalibreConverter>.Instance);
        var reader = new BookTextReader(fs, converter, NullLogger<BookTextReader>.Instance);
        return new ContentScanService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new BackgroundTaskCoordinator(),
            reader,
            NullLogger<ContentScanService>.Instance);
    }

    private sealed class NoopRunner : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(System.Diagnostics.ProcessStartInfo s, System.TimeSpan t, CancellationToken ct)
            => Task.FromResult(new ProcessRunResult(0, "", ""));
    }
}

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

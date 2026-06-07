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

namespace TheLibrary.Server.Tests;

public class BookIntegrityServiceTests
{
    [Fact]
    public async Task Run_Flags_Short_File_As_Damaged_And_Records_Fingerprint()
    {
        var fs = new FakeFileSystem();
        fs.AddFile("/lib/tiny.epub", TestEpub.Build(("c.xhtml", TestEpub.HtmlWithText(1_000))));
        var dbName = Seed(("/lib/tiny.epub", sizeBytes: 10, priority: 0));
        var sut = CreateService(fs, dbName);

        var summary = await sut.RunForTestsAsync(CancellationToken.None);

        Assert.Equal(1, summary.Checked);
        Assert.Equal(1, summary.Damaged);

        await using var db = CreateDb(dbName);
        var file = await db.LocalBookFiles.SingleAsync();
        Assert.False(file.IntegrityOk);
        Assert.Equal(10, file.IntegrityCheckedSize);  // stored so it isn't re-checked
        Assert.NotNull(file.IntegrityError);
    }

    [Fact]
    public async Task Run_Skips_Files_Already_Checked_At_Current_Size()
    {
        var fs = new FakeFileSystem();
        fs.AddFile("/lib/ok.epub", TestEpub.Build(("c.xhtml", TestEpub.HtmlWithText(40_000))));
        // Already checked at the current size → not a candidate.
        var dbName = Seed(("/lib/ok.epub", sizeBytes: 99, priority: 0), checkedSize: 99);
        var sut = CreateService(fs, dbName);

        var summary = await sut.RunForTestsAsync(CancellationToken.None);

        Assert.Equal(0, summary.Checked);
    }

    [Fact]
    public async Task Run_Rechecks_When_Modified_Time_Changes_Even_If_Size_Matches()
    {
        var fs = new FakeFileSystem();
        // Short epub → will be flagged damaged once it's re-checked.
        fs.AddFile("/lib/ok.epub", TestEpub.Build(("c.xhtml", TestEpub.HtmlWithText(1_000))));

        var dbName = NewDb();
        await using (var db = CreateDb(dbName))
        {
            var author = new Author { Name = "A", Priority = 0 };
            var book = new Book { Title = "B", Author = author };
            db.LocalBookFiles.Add(new LocalBookFile
            {
                FullPath = "/lib/ok.epub",
                SizeBytes = 50,
                ModifiedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), // newer than last check
                Book = book,
                Author = author,
                IntegrityOk = true,                       // previously marked OK
                IntegrityCheckedSize = 50,                // size unchanged…
                IntegrityCheckedModified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), // …but mtime older
            });
            await db.SaveChangesAsync();
        }

        var summary = await CreateService(fs, dbName).RunForTestsAsync(CancellationToken.None);

        Assert.Equal(1, summary.Checked); // re-checked because mtime advanced
        await using var verify = CreateDb(dbName);
        var file = await verify.LocalBookFiles.SingleAsync();
        Assert.False(file.IntegrityOk);
        Assert.Equal(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), file.IntegrityCheckedModified);
    }

    [Fact]
    public async Task Run_Processes_Untracked_Files_When_No_Author_Linked_Remain()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory("/lib");
        fs.AddFile("/lib/__unknown/bad.epub", TestEpub.Build(("c.xhtml", TestEpub.HtmlWithText(500))));     // damaged
        fs.AddFile("/lib/__unknown/good.epub", TestEpub.Build(("c.xhtml", TestEpub.HtmlWithText(40_000)))); // healthy

        var dbName = NewDb();
        var mtime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await using (var db = CreateDb(dbName))
        {
            db.LibraryLocations.Add(new LibraryLocation { Path = "/lib", Enabled = true });
            // No LocalBookFiles → phase 1 is empty → the untracked phase runs.
            db.UnknownFiles.AddRange(
                new UnknownFile { FullPath = "/lib/__unknown/bad.epub", FileName = "bad.epub", SizeBytes = 1, ModifiedAt = mtime },
                new UnknownFile { FullPath = "/lib/__unknown/good.epub", FileName = "good.epub", SizeBytes = 1, ModifiedAt = mtime });
            await db.SaveChangesAsync();
        }

        var summary = await CreateService(fs, dbName).RunForTestsAsync(CancellationToken.None);

        Assert.Equal(2, summary.Checked);
        Assert.Equal(1, summary.Damaged);

        // Damaged untracked file was archived (moved out of __unknown).
        Assert.False(fs.FileExists("/lib/__unknown/bad.epub"));
        Assert.Contains(fs.ExistingFiles, p => p.Contains("__archive") && p.Contains("bad"));
        // Healthy one stays put and is recorded so it isn't re-checked next run.
        Assert.True(fs.FileExists("/lib/__unknown/good.epub"));
        await using var verify = CreateDb(dbName);
        Assert.Contains(await verify.UnknownFileChecks.ToListAsync(), c => c.FullPath == "/lib/__unknown/good.epub");
    }

    [Fact]
    public async Task Run_Archives_Damaged_Unmatched_Author_Linked_File()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory("/lib");
        fs.AddFile("/lib/A/orphan.epub", TestEpub.Build(("c.xhtml", TestEpub.HtmlWithText(500)))); // too short → damaged

        var dbName = NewDb();
        await using (var db = CreateDb(dbName))
        {
            db.LibraryLocations.Add(new LibraryLocation { Path = "/lib", Enabled = true });
            db.LocalBookFiles.Add(new LocalBookFile
            {
                FullPath = "/lib/A/orphan.epub",
                SizeBytes = 1,
                ModifiedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Author = new Author { Name = "Orphan Author" }, // AuthorId set, BookId null
            });
            await db.SaveChangesAsync();
        }

        var summary = await CreateService(fs, dbName).RunForTestsAsync(CancellationToken.None);

        Assert.Equal(1, summary.Damaged);
        await using var verify = CreateDb(dbName);
        var file = await verify.LocalBookFiles.SingleAsync();
        Assert.False(file.IntegrityOk);
        Assert.Contains("__archive", file.FullPath);          // moved to the archive folder
        Assert.False(fs.FileExists("/lib/A/orphan.epub"));    // gone from its old path
        Assert.True(fs.FileExists(file.FullPath));
    }

    [Fact]
    public async Task Run_Prefers_Unarchived_Over_Starred_Archived_When_Capped()
    {
        var fs = new FakeFileSystem();
        var epub = TestEpub.Build(("c.xhtml", TestEpub.HtmlWithText(40_000)));
        fs.AddFile("/lib/__archive/A/book.epub", epub); // starred author, but archived
        fs.AddFile("/lib/B/book.epub", epub);           // unstarred author, but live

        var dbName = Seed(
            new[]
            {
                ("/lib/__archive/A/book.epub", 1L, 5), // priority 5 (starred), archived
                ("/lib/B/book.epub", 1L, 0),           // priority 0, unarchived
            },
            maxPerRun: 1);

        var summary = await CreateService(fs, dbName).RunForTestsAsync(CancellationToken.None);

        Assert.Equal(1, summary.Checked);
        await using var db = CreateDb(dbName);
        var live = await db.LocalBookFiles.SingleAsync(f => f.FullPath == "/lib/B/book.epub");
        var archived = await db.LocalBookFiles.SingleAsync(f => f.FullPath == "/lib/__archive/A/book.epub");
        Assert.NotNull(live.IntegrityCheckedAt);  // unarchived wins the slot…
        Assert.Null(archived.IntegrityCheckedAt); // …even though the archived copy is starred
    }

    [Fact]
    public async Task Run_Checks_Starred_Author_Files_First_When_Capped()
    {
        var fs = new FakeFileSystem();
        fs.AddFile("/lib/normal.epub", TestEpub.Build(("c.xhtml", TestEpub.HtmlWithText(40_000))));
        fs.AddFile("/lib/starred.epub", TestEpub.Build(("c.xhtml", TestEpub.HtmlWithText(40_000))));
        // Normal author seeded first (lower Id) but unstarred; starred author's
        // file must still win the single per-run slot.
        var dbName = Seed(
            new[]
            {
                ("/lib/normal.epub", 1L, 0),
                ("/lib/starred.epub", 1L, 3),
            },
            maxPerRun: 1);
        var sut = CreateService(fs, dbName);

        var summary = await sut.RunForTestsAsync(CancellationToken.None);

        Assert.Equal(1, summary.Checked);
        await using var db = CreateDb(dbName);
        var starred = await db.LocalBookFiles.SingleAsync(f => f.FullPath == "/lib/starred.epub");
        var normal = await db.LocalBookFiles.SingleAsync(f => f.FullPath == "/lib/normal.epub");
        Assert.NotNull(starred.IntegrityCheckedAt);     // checked this run
        Assert.Null(normal.IntegrityCheckedAt);         // deferred to a later run
    }

    [Fact]
    public async Task Run_Checks_All_Files_Of_The_Same_Book_Even_Past_The_Per_Run_Cap()
    {
        var fs = new FakeFileSystem();
        fs.AddFile("/lib/copy1.epub", TestEpub.Build(("c.xhtml", TestEpub.HtmlWithText(40_000)))); // healthy
        fs.AddFile("/lib/copy2.epub", TestEpub.Build(("c.xhtml", TestEpub.HtmlWithText(1_000))));  // damaged

        var dbName = NewDb();
        await using (var db = CreateDb(dbName))
        {
            var author = new Author { Name = "A", Priority = 0 };
            var book = new Book { Title = "Shared Title", Author = author };
            db.LocalBookFiles.AddRange(
                new LocalBookFile { FullPath = "/lib/copy1.epub", SizeBytes = 10, ModifiedAt = FixedModified, Book = book, Author = author },
                new LocalBookFile { FullPath = "/lib/copy2.epub", SizeBytes = 11, ModifiedAt = FixedModified, Book = book, Author = author });
            // Cap the run at a single file — the second copy must still be checked
            // because it belongs to the same book as the first.
            db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.IntegrityMaxBooksPerRun, Value = "1" });
            await db.SaveChangesAsync();
        }
        var sut = CreateService(fs, dbName);

        var summary = await sut.RunForTestsAsync(CancellationToken.None);

        Assert.Equal(2, summary.Checked); // both copies, despite max = 1
        await using var verify = CreateDb(dbName);
        Assert.NotNull((await verify.LocalBookFiles.SingleAsync(f => f.FullPath == "/lib/copy1.epub")).IntegrityCheckedAt);
        Assert.NotNull((await verify.LocalBookFiles.SingleAsync(f => f.FullPath == "/lib/copy2.epub")).IntegrityCheckedAt);
    }

    [Fact]
    public async Task Run_Harvests_Series_Catalogue_From_Matched_Book_During_Integrity()
    {
        var fs = new FakeFileSystem();
        // Healthy book whose back matter lists the author's series.
        fs.AddFile("/lib/A/book.epub", TestEpub.Build(
            ("body.xhtml", TestEpub.HtmlWithText(50_000)),
            ("alsoby.xhtml", "<html><body><p>Also by Test Author</p><p>The Saga Series (Fantasy)</p>"
                + "<p>Book One</p><p>Book Two</p></body></html>")));

        var dbName = NewDb();
        string path = "/lib/A/book.epub";
        await using (var db = CreateDb(dbName))
        {
            var author = new Author { Name = "Test Author" };
            var book = new Book { Title = "Matched Book", Author = author };
            db.LocalBookFiles.Add(new LocalBookFile
            {
                FullPath = path, SizeBytes = 10, ModifiedAt = FixedModified, Book = book, Author = author,
            });
            await db.SaveChangesAsync();
        }
        var sut = CreateService(fs, dbName);

        await sut.RunForTestsAsync(CancellationToken.None);

        await using var verify = CreateDb(dbName);
        var scan = await verify.BookContentScans.SingleAsync(c => c.FullPath == path);
        Assert.NotNull(scan.SeriesCatalogJson);          // catalogue harvested in the integrity pass
        Assert.Contains("The Saga", scan.SeriesCatalogJson!);
        Assert.Null(scan.Title);                          // matched book → catalogue-only, no redundant guess
        Assert.Null(scan.Author);
    }

    [Fact]
    public async Task Run_Leaves_NonNative_Files_Untouched_When_Calibre_Not_Configured()
    {
        var fs = new FakeFileSystem();
        fs.AddFile("/lib/book.mobi", new byte[] { 1, 2, 3 });
        var dbName = Seed(("/lib/book.mobi", sizeBytes: 5, priority: 0));
        var sut = CreateService(fs, dbName, calibreConfigured: false);

        var summary = await sut.RunForTestsAsync(CancellationToken.None);

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, summary.Checked);
        await using var db = CreateDb(dbName);
        var file = await db.LocalBookFiles.SingleAsync();
        Assert.Null(file.IntegrityCheckedSize); // untouched → retried once Calibre is set up
    }

    // --- helpers ----------------------------------------------------------

    private static string Seed((string Path, long SizeBytes, int Priority) file, long? checkedSize = null)
        => Seed(new[] { file }, checkedSize: checkedSize);

    // Every seeded file has this modified time; a "checked" file is stamped with
    // the same value so it isn't re-checked unless a test changes one of them.
    private static readonly DateTime FixedModified = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static string Seed(
        (string Path, long SizeBytes, int Priority)[] files,
        long? checkedSize = null,
        int? maxPerRun = null)
    {
        var dbName = $"integrity-tests-{Guid.NewGuid():N}";
        using var db = CreateDb(dbName);
        var id = 1;
        foreach (var (path, size, priority) in files)
        {
            var author = new Author { Name = $"Author {id}", Priority = priority };
            var book = new Book { Title = $"Book {id}", Author = author };
            db.LocalBookFiles.Add(new LocalBookFile
            {
                FullPath = path,
                SizeBytes = size,
                ModifiedAt = FixedModified,
                Book = book,
                Author = author,
                IntegrityCheckedSize = checkedSize,
                IntegrityCheckedModified = checkedSize is null ? null : FixedModified,
            });
            id++;
        }
        if (maxPerRun is not null)
            db.AppSettings.Add(new AppSetting
            {
                Key = AppSettingKeys.IntegrityMaxBooksPerRun,
                Value = maxPerRun.Value.ToString(),
            });
        db.SaveChanges();
        return dbName;
    }

    private static string NewDb() => $"integrity-tests-{Guid.NewGuid():N}";

    private static LibraryDbContext CreateDb(string name)
        => new(new DbContextOptionsBuilder<LibraryDbContext>().UseInMemoryDatabase(name).Options);

    private static BookIntegrityService CreateService(FakeFileSystem fs, string dbName, bool calibreConfigured = false)
    {
        var services = new ServiceCollection();
        services.AddDbContext<LibraryDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();

        var converter = new CalibreConverter(
            Options.Create(new CalibreOptions { EbookConvert = calibreConfigured ? "ebook-convert" : null }),
            fs, new NoopProcessRunner(), NullLogger<CalibreConverter>.Instance);
        var checker = new BookIntegrityChecker(converter, fs, NullLogger<BookIntegrityChecker>.Instance);
        var reader = new BookTextReader(fs, converter, NullLogger<BookTextReader>.Instance);
        var contentScan = new ContentScanService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new BackgroundTaskCoordinator(),
            reader,
            NullLogger<ContentScanService>.Instance);

        return new BookIntegrityService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new BackgroundTaskCoordinator(),
            checker,
            contentScan,
            fs,
            NullLogger<BookIntegrityService>.Instance);
    }

    private sealed class NoopProcessRunner : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(System.Diagnostics.ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken ct)
            => Task.FromResult(new ProcessRunResult(0, "", ""));
    }
}

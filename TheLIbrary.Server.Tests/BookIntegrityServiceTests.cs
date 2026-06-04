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
                Book = book,
                Author = author,
                IntegrityCheckedSize = checkedSize,
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

        return new BookIntegrityService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new BackgroundTaskCoordinator(),
            checker,
            NullLogger<BookIntegrityService>.Instance);
    }

    private sealed class NoopProcessRunner : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(System.Diagnostics.ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken ct)
            => Task.FromResult(new ProcessRunResult(0, "", ""));
    }
}

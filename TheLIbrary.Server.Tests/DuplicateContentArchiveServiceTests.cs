using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.IO;
using TheLibrary.Server.Services.Scheduling;
using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

// Verifies the content-verified duplicate archiver: it only performs the
// archive when EVERY extra copy's extracted text matches the keeper's within
// the configured threshold, and leaves the entire group untouched the moment
// any copy falls short (including when it can't be read at all) — against
// real temp files so extraction + move + source-removal actually run.
public sealed class DuplicateContentArchiveServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tl-dupcontent-" + Guid.NewGuid().ToString("N"));
    private readonly string _authorDir;

    public DuplicateContentArchiveServiceTests()
    {
        _authorDir = Path.Combine(_root, "Auth");
        Directory.CreateDirectory(_authorDir);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static string SharedBody(string suffix = "") =>
        string.Join(' ', Enumerable.Range(0, 40).Select(i => $"paragraph{ToAlpha(i)}{suffix}"));

    private static string ToAlpha(int n)
    {
        var sb = new System.Text.StringBuilder();
        do { sb.Insert(0, (char)('a' + n % 26)); n = n / 26 - 1; } while (n >= 0);
        return sb.ToString();
    }

    private async Task<(string dbName, string epub, string pdf)> SeedTwoCopyBookAsync(
        string epubText, string pdfText, bool asTxt = true)
    {
        var dbName = "dupcontent-" + Guid.NewGuid().ToString("N");
        var ext1 = asTxt ? "txt" : "epub";
        var ext2 = asTxt ? "txt" : "pdf";
        var f1 = Path.Combine(_authorDir, $"book.{ext1}");
        var f2 = Path.Combine(_authorDir, $"book2.{ext2}");
        await File.WriteAllTextAsync(f1, epubText);
        await File.WriteAllTextAsync(f2, pdfText);

        await using var seed = new LibraryDbContext(new DbContextOptionsBuilder<LibraryDbContext>().UseInMemoryDatabase(dbName).Options);
        seed.LibraryLocations.Add(new LibraryLocation { Id = 1, Label = "L", Path = _root, Enabled = true, IsPrimary = true, CreatedAt = DateTime.UtcNow });
        seed.Authors.Add(new Author { Id = 1, Name = "Auth" });
        seed.Books.Add(new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W", Title = "Book", NormalizedTitle = "book" });
        seed.LocalBookFiles.AddRange(
            new LocalBookFile { Id = 1, BookId = 10, AuthorId = 1, AuthorFolder = "Auth", FullPath = f1, IntegrityOk = true, ModifiedAt = DateTime.UtcNow },
            new LocalBookFile { Id = 2, BookId = 10, AuthorId = 1, AuthorFolder = "Auth", FullPath = f2, IntegrityOk = true, ModifiedAt = DateTime.UtcNow });
        await seed.SaveChangesAsync();
        return (dbName, f1, f2);
    }

    private static DuplicateContentArchiveService CreateService(string dbName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<LibraryDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();
        var fs = new SystemFileSystem();
        var converter = new CalibreConverter(Options.Create(new CalibreOptions()), fs, new NoopRunner(), NullLogger<CalibreConverter>.Instance);
        var reader = new BookTextReader(fs, converter, NullLogger<BookTextReader>.Instance);
        return new DuplicateContentArchiveService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new BackgroundTaskCoordinator(), reader, fs,
            NullLogger<DuplicateContentArchiveService>.Instance);
    }

    [Fact]
    public async Task Matching_Content_Archives_The_Extra_And_Logs_Activity()
    {
        var body = SharedBody();
        var (dbName, keep, extra) = await SeedTwoCopyBookAsync(body, body);
        var sut = CreateService(dbName);

        var result = await sut.RunForTestsAsync(CancellationToken.None);

        Assert.Equal(1, result.GroupsArchived);
        Assert.Equal(1, result.FilesArchived);
        Assert.Equal(0, result.GroupsSkippedMismatch);

        await using var verify = new LibraryDbContext(new DbContextOptionsBuilder<LibraryDbContext>().UseInMemoryDatabase(dbName).Options);
        var keeperRow = await verify.LocalBookFiles.FindAsync(1);
        var extraRow = await verify.LocalBookFiles.FindAsync(2);
        Assert.Equal(keep.Replace('\\', '/'), keeperRow!.FullPath.Replace('\\', '/'));
        Assert.Contains("__archive", extraRow!.FullPath.Replace('\\', '/'));
        Assert.False(File.Exists(extra));
        Assert.True(File.Exists(extraRow.FullPath));
        Assert.True(File.Exists(keep));
        Assert.True(await verify.ActivityLog.AnyAsync(a => a.Action == "archive" && a.Source == "verify-archive-duplicates"));

        // Per-title check line records the actual match percentage, not just the outcome.
        var checkLine = await verify.ActivityLog.SingleAsync(a => a.Action == "verify-archive-duplicates" && a.BookId == 10);
        Assert.Contains("100%", checkLine.Detail);
        Assert.Contains("archived", checkLine.Detail);
    }

    [Fact]
    public async Task Mismatched_Content_Leaves_The_Whole_Group_Untouched()
    {
        var (dbName, keep, extra) = await SeedTwoCopyBookAsync(SharedBody("alpha-topic"), SharedBody("zzz-unrelated-topic"));
        var sut = CreateService(dbName);

        var result = await sut.RunForTestsAsync(CancellationToken.None);

        Assert.Equal(0, result.GroupsArchived);
        Assert.Equal(0, result.FilesArchived);
        Assert.Equal(1, result.GroupsSkippedMismatch);

        // Neither file moved — a mismatch on any copy means NOTHING happens.
        Assert.True(File.Exists(keep));
        Assert.True(File.Exists(extra));

        // No archive happened, so no "archive" summary line — but the per-title
        // check line (with its computed percentage) is still recorded, so a low
        // match rate is visible even when nothing gets archived.
        await using var verify = new LibraryDbContext(new DbContextOptionsBuilder<LibraryDbContext>().UseInMemoryDatabase(dbName).Options);
        Assert.False(await verify.ActivityLog.AnyAsync(a => a.Action == "archive"));
        var checkLine = await verify.ActivityLog.SingleAsync(a => a.Action == "verify-archive-duplicates");
        Assert.Contains("left untouched", checkLine.Detail);
        Assert.Contains("%", checkLine.Detail);
    }

    [Fact]
    public async Task Unreadable_Extra_Is_Treated_As_Unverifiable_And_Skips_The_Group()
    {
        // .cbz has no text extractor (image-only format) — BookTextReader
        // returns "" for it, which must be treated the same as a mismatch,
        // never as an accidental pass.
        var dbName = "dupcontent-" + Guid.NewGuid().ToString("N");
        var epub = Path.Combine(_authorDir, "book.txt");
        var cbz = Path.Combine(_authorDir, "book.cbz");
        await File.WriteAllTextAsync(epub, SharedBody());
        await File.WriteAllTextAsync(cbz, "not really a cbz, just bytes");

        await using (var seed = new LibraryDbContext(new DbContextOptionsBuilder<LibraryDbContext>().UseInMemoryDatabase(dbName).Options))
        {
            seed.LibraryLocations.Add(new LibraryLocation { Id = 1, Label = "L", Path = _root, Enabled = true, IsPrimary = true, CreatedAt = DateTime.UtcNow });
            seed.Authors.Add(new Author { Id = 1, Name = "Auth" });
            seed.Books.Add(new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W", Title = "Book", NormalizedTitle = "book" });
            seed.LocalBookFiles.AddRange(
                new LocalBookFile { Id = 1, BookId = 10, AuthorId = 1, AuthorFolder = "Auth", FullPath = epub, IntegrityOk = true, ModifiedAt = DateTime.UtcNow },
                new LocalBookFile { Id = 2, BookId = 10, AuthorId = 1, AuthorFolder = "Auth", FullPath = cbz, IntegrityOk = true, ModifiedAt = DateTime.UtcNow });
            await seed.SaveChangesAsync();
        }

        var sut = CreateService(dbName);
        var result = await sut.RunForTestsAsync(CancellationToken.None);

        Assert.Equal(0, result.GroupsArchived);
        Assert.Equal(1, result.GroupsSkippedMismatch);
        Assert.True(File.Exists(epub));
        Assert.True(File.Exists(cbz));

        // Unreadable text shows up as "n/a" in the log, not a misleading 0% or
        // a silently dropped comparison — the point is to make a bad extraction
        // visible, not just a bad match.
        await using var verify = new LibraryDbContext(new DbContextOptionsBuilder<LibraryDbContext>().UseInMemoryDatabase(dbName).Options);
        var checkLine = await verify.ActivityLog.SingleAsync(a => a.Action == "verify-archive-duplicates");
        Assert.Contains("n/a", checkLine.Detail);
    }

    [Fact]
    public async Task Respects_A_Custom_Threshold_Setting()
    {
        // Overlapping enough to clear the 90% default but not a stricter 99.9%.
        var shared = string.Join(' ', Enumerable.Range(0, 90).Select(i => $"word{ToAlpha(i)}"));
        var a = shared + " onlyA";
        var b = shared + " onlyB";
        var (dbName, keep, extra) = await SeedTwoCopyBookAsync(a, b);

        await using (var db = new LibraryDbContext(new DbContextOptionsBuilder<LibraryDbContext>().UseInMemoryDatabase(dbName).Options))
        {
            db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.DuplicateContentMatchThreshold, Value = "99.9" });
            await db.SaveChangesAsync();
        }

        var sut = CreateService(dbName);
        var result = await sut.RunForTestsAsync(CancellationToken.None);

        Assert.Equal(0, result.GroupsArchived);
        Assert.Equal(1, result.GroupsSkippedMismatch);
        Assert.True(File.Exists(keep));
        Assert.True(File.Exists(extra));
    }

    private sealed class NoopRunner : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(System.Diagnostics.ProcessStartInfo s, TimeSpan t, CancellationToken ct)
            => Task.FromResult(new ProcessRunResult(0, "", ""));
    }
}

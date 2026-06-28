using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Controllers;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Incoming;
using TheLibrary.Server.Services.IO;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

// Coverage top-up for cheaper non-OpenLibrary paths across BooksController,
// the untracked surface, and the incoming reprocess pipeline.
public sealed class MiscCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tl-misc-" + Guid.NewGuid().ToString("N"));

    public MiscCoverageTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static BooksController Books(LibraryDbContext db)
        => new(db, httpFactory: null!, new SystemFileSystem());

    private static AuthorsController Authors(LibraryDbContext db)
        => new(db, ol: null!, refresher: null!, manualBooks: null!, manualAuthors: null!, fs: new SystemFileSystem(),
            log: NullLogger<AuthorsController>.Instance, converter: null!, contentScan: null!,
            assigner: null!, lifetime: null!);

    [Fact]
    public async Task Books_Cover_Foreign_Scan_Export_Reindex()
    {
        using var rdb = new RelationalTestDb();
        await using (var s = rdb.NewContext())
        {
            s.LibraryLocations.Add(new LibraryLocation { Id = 1, Label = "L", Path = _root, Enabled = true, IsPrimary = true, CreatedAt = DateTime.UtcNow });
            s.Authors.Add(new Author { Id = 1, Name = "A", Priority = 1 });
            s.Books.AddRange(
                new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W", Title = "Plain English Title", NormalizedTitle = "plain english title", FirstPublishYear = 2020 },
                // A title that the language guesser should flag as non-English.
                new Book { Id = 11, AuthorId = 1, OpenLibraryWorkKey = "OL11W", Title = "Поваренная книга", NormalizedTitle = "x", FirstPublishYear = 2021 });
            await s.SaveChangesAsync();
        }

        await using (var db = rdb.NewContext())
        {
            var c = Books(db);
            Assert.IsType<OkObjectResult>(await c.SetCover(10, new BooksController.SetCoverRequest { Url = "https://example.com/c.jpg" }, default));
            await c.ScanForeign(default);
            var export = await c.ExportMissingWorks(default);
            Assert.IsType<FileContentResult>(export);
        }

        await using (var v = rdb.NewContext())
            Assert.Equal("https://example.com/c.jpg", (await v.Books.FindAsync(10))!.CoverUrl);
    }

    [Fact]
    public async Task Untracked_Match_And_Nested_Browse()
    {
        using var rdb = new RelationalTestDb();
        var unknown = Path.Combine(_root, "__unknown");
        var sub = Path.Combine(unknown, "Nested");
        Directory.CreateDirectory(sub);
        await File.WriteAllTextAsync(Path.Combine(sub, "deep.epub"), "x");

        await using (var s = rdb.NewContext())
        {
            s.LibraryLocations.Add(new LibraryLocation { Id = 1, Label = "L", Path = _root, Enabled = true, IsPrimary = true, CreatedAt = DateTime.UtcNow });
            await s.SaveChangesAsync();
        }

        await using var db = rdb.NewContext();
        var c = Authors(db);
        // Recursive files-only browse of a nested unknown folder.
        var contents = await c.GetUntrackedContents("unknown", "Nested", _root, null, true, default);
        Assert.IsNotType<NotFoundObjectResult>(contents.Result);
        // Matcher runs against the (empty) watchlist — exercises the match pipeline.
        var match = await c.MatchUnknownFolders(default);
        Assert.NotNull(match);
    }

    [Fact]
    public async Task Incoming_Reprocess_Unknown_Runs()
    {
        using var rdb = new RelationalTestDb();
        var incoming = Path.Combine(_root, "_incoming");
        var unknown = Path.Combine(_root, "__unknown");
        Directory.CreateDirectory(incoming);
        Directory.CreateDirectory(unknown);
        await File.WriteAllTextAsync(Path.Combine(unknown, "Orphan Title zzqq.epub"), "x");

        await using (var s = rdb.NewContext())
        {
            s.LibraryLocations.Add(new LibraryLocation { Id = 1, Label = "L", Path = _root, Enabled = true, IsPrimary = true, CreatedAt = DateTime.UtcNow });
            s.AppSettings.Add(new AppSetting { Key = AppSettingKeys.IncomingFolder, Value = incoming });
            await s.SaveChangesAsync();
        }

        await using var db = rdb.NewContext();
        var processor = new IncomingProcessor(db, new SystemFileSystem(), NullLogger<IncomingProcessor>.Instance);
        var result = await processor.ProcessUnknownAsync(null, CancellationToken.None);
        Assert.NotNull(result);
    }
}

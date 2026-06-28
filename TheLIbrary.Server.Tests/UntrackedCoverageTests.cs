using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Controllers;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.IO;
using TheLibrary.Server.Services.Sync;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

// Coverage for the untracked/quarantine surface (AuthorsController.Untracked.cs),
// which reads and mutates the real filesystem. Uses real temp directories + the
// real SystemFileSystem so the System.IO scans and the _fs moves operate on the
// same files, and a relational SQLite DB so the queries/ExecuteUpdate paths run.
public sealed class UntrackedCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tl-untracked-" + Guid.NewGuid().ToString("N"));
    private readonly string _incoming;

    public UntrackedCoverageTests()
    {
        _incoming = Path.Combine(_root, "_incoming");
        Directory.CreateDirectory(_incoming);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static AuthorsController NewController(LibraryDbContext db)
        => new(db, ol: null!, refresher: null!, manualBooks: null!, manualAuthors: null!, fs: new SystemFileSystem(),
            log: NullLogger<AuthorsController>.Instance, converter: null!, contentScan: null!,
            assigner: null!, lifetime: null!);

    private async Task SeedAsync(RelationalTestDb rdb)
    {
        // Disk layout: an unclaimed author folder with a book, plus a populated
        // __unknown bucket (one loose file + one sub-folder with a file).
        var unclaimedDir = Path.Combine(_root, "Unclaimed Auth");
        Directory.CreateDirectory(unclaimedDir);
        await File.WriteAllTextAsync(Path.Combine(unclaimedDir, "book.epub"), "x");

        var unknown = Path.Combine(_root, "__unknown");
        Directory.CreateDirectory(unknown);
        await File.WriteAllTextAsync(Path.Combine(unknown, "Loose Title.epub"), "x");
        var unkSub = Path.Combine(unknown, "Some Folder");
        Directory.CreateDirectory(unkSub);
        await File.WriteAllTextAsync(Path.Combine(unkSub, "inner.epub"), "x");

        await using var s = rdb.NewContext();
        s.LibraryLocations.Add(new LibraryLocation { Id = 1, Label = "L", Path = _root, Enabled = true, IsPrimary = true, CreatedAt = DateTime.UtcNow });
        s.AppSettings.Add(new AppSetting { Key = AppSettingKeys.IncomingFolder, Value = _incoming });
        s.OpenLibraryAuthors.Add(new OpenLibraryAuthor { OlKey = "OL1A", Name = "Unclaimed Auth", NormalizedName = TitleNormalizer.NormalizeAuthor("Unclaimed Auth"), ImportedAt = DateTime.UtcNow });
        s.LocalBookFiles.Add(new LocalBookFile { Id = 1, AuthorId = null, AuthorFolder = "Unclaimed Auth", TitleFolder = "book", FullPath = Path.Combine(unclaimedDir, "book.epub"), ModifiedAt = DateTime.UtcNow });
        await s.SaveChangesAsync();
    }

    [Fact]
    public async Task Listings_Reflect_Disk()
    {
        using var rdb = new RelationalTestDb();
        await SeedAsync(rdb);
        await using var db = rdb.NewContext();
        var c = NewController(db);

        var unclaimed = await c.Unclaimed(default);
        Assert.Contains(unclaimed, u => u.AuthorFolder == "Unclaimed Auth");

        var unknown = await c.ListUnknownFolders(default);
        Assert.Contains(unknown, u => u.AuthorFolder == "Loose Title.epub" && u.IsFile);
        Assert.Contains(unknown, u => u.AuthorFolder == "Some Folder" && !u.IsFile);
    }

    [Fact]
    public async Task Browse_Then_Delete_Loose_File()
    {
        using var rdb = new RelationalTestDb();
        await SeedAsync(rdb);
        await using var db = rdb.NewContext();
        var c = NewController(db);

        var contents = await c.GetUntrackedContents("unknown", "Some Folder", _root, null, false, default);
        Assert.IsNotType<Microsoft.AspNetCore.Mvc.NotFoundObjectResult>(contents.Result);

        await c.DeleteUntrackedPath("unknown", "Loose Title.epub", _root, null, default);
        Assert.False(File.Exists(Path.Combine(_root, "__unknown", "Loose Title.epub")));
    }

    [Fact]
    public async Task Return_Unknown_Folder_To_Incoming()
    {
        using var rdb = new RelationalTestDb();
        await SeedAsync(rdb);
        await using var db = rdb.NewContext();

        var result = await NewController(db).ReturnUnknownFolder("Some Folder", default);
        Assert.IsNotType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result);
        // The folder left __unknown.
        Assert.False(Directory.Exists(Path.Combine(_root, "__unknown", "Some Folder")));
    }

    [Fact]
    public async Task Discard_Unclaimed_Moves_To_Incoming_And_Drops_Rows()
    {
        using var rdb = new RelationalTestDb();
        await SeedAsync(rdb);

        await using (var db = rdb.NewContext())
        {
            var result = await NewController(db).DiscardUnclaimed("Unclaimed Auth", default);
            Assert.IsNotType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result);
        }
        await using (var v = rdb.NewContext())
            Assert.Empty(v.LocalBookFiles.Where(f => f.AuthorFolder == "Unclaimed Auth"));
    }

    [Fact]
    public async Task Bulk_Return_And_Discard_All_Run()
    {
        using var rdb = new RelationalTestDb();
        await SeedAsync(rdb);
        await using var db = rdb.NewContext();
        var c = NewController(db);

        Assert.IsNotType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(await c.ReturnAllUnknownFolders(default));
        Assert.IsNotType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(await c.DiscardAllUnclaimed(default));
    }
}

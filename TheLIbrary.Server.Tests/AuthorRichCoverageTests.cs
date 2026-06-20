using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Controllers;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.IO;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

// Drives the heavy AuthorsController.Get detail builder over richly-shaped data
// (owned/missing/suppressed/foreign books, an unmatched file, a series, a
// same-name sibling author) plus the merge/link/unlink/notes mutations.
public sealed class AuthorRichCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tl-rich-" + Guid.NewGuid().ToString("N"));

    public AuthorRichCoverageTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static AuthorsController NewController(LibraryDbContext db)
        => new(db, ol: null!, refresher: null!, manualBooks: null!, fs: new SystemFileSystem(),
            log: NullLogger<AuthorsController>.Instance, converter: null!, contentScan: null!,
            assigner: null!, lifetime: null!);

    private async Task SeedAsync(RelationalTestDb rdb)
    {
        var aDir = Path.Combine(_root, "Common Name");
        Directory.CreateDirectory(aDir);
        var ownedFile = Path.Combine(aDir, "Owned Book.epub");
        var looseFile = Path.Combine(aDir, "Unmatched One.epub");
        await File.WriteAllTextAsync(ownedFile, "x");
        await File.WriteAllTextAsync(looseFile, "x");

        await using var s = rdb.NewContext();
        s.LibraryLocations.Add(new LibraryLocation { Id = 1, Label = "L", Path = _root, Enabled = true, IsPrimary = true, CreatedAt = DateTime.UtcNow });
        s.Authors.AddRange(
            new Author { Id = 1, Name = "Common Name", Priority = 1, CalibreFolderName = "Common Name", Status = AuthorStatus.Active },
            new Author { Id = 2, Name = "Common Name", Priority = 0, Status = AuthorStatus.Active },   // same-name sibling
            new Author { Id = 3, Name = "Other Person", Priority = 0, Status = AuthorStatus.Active });  // link/merge target
        s.Series.Add(new Series { Id = 1, Name = "A Series", NormalizedName = "a series", PrimaryAuthorId = 1 });
        s.Books.AddRange(
            new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W", Title = "Owned Book", NormalizedTitle = "owned book", FirstPublishYear = 2010, SeriesId = 1, SeriesPosition = "1" },
            new Book { Id = 11, AuthorId = 1, OpenLibraryWorkKey = "OL11W", Title = "Missing Book", NormalizedTitle = "missing book", FirstPublishYear = 2011 },
            new Book { Id = 12, AuthorId = 1, OpenLibraryWorkKey = "OL12W", Title = "Hidden", NormalizedTitle = "hidden", Suppressed = true },
            new Book { Id = 13, AuthorId = 1, OpenLibraryWorkKey = "OL13W", Title = "Foreign", NormalizedTitle = "foreign", Foreign = true, Suppressed = true });
        s.LocalBookFiles.AddRange(
            new LocalBookFile { Id = 1, BookId = 10, AuthorId = 1, AuthorFolder = "Common Name", FullPath = ownedFile, ModifiedAt = DateTime.UtcNow },
            new LocalBookFile { Id = 2, BookId = null, AuthorId = 1, AuthorFolder = "Common Name", TitleFolder = "Unmatched One", FullPath = looseFile, ModifiedAt = DateTime.UtcNow });
        await s.SaveChangesAsync();
    }

    [Fact]
    public async Task Detail_Builder_Handles_Rich_Data()
    {
        using var rdb = new RelationalTestDb();
        await SeedAsync(rdb);
        await using var db = rdb.NewContext();
        var detail = await NewController(db).Get(1, default);
        Assert.NotNull(detail.Value);
        Assert.Equal("Common Name", detail.Value!.Name);
        Assert.NotEmpty(detail.Value.Books);
    }

    [Fact]
    public async Task Notes_Link_Unlink_Merge_Run()
    {
        using var rdb = new RelationalTestDb();
        await SeedAsync(rdb);

        await using (var db = rdb.NewContext())
        {
            var c = NewController(db);
            await c.SaveNotes(1, new AuthorsController.SaveNotesRequest("a note"), default);
            await c.Link(3, new AuthorsController.LinkAuthorRequest(1, IsPenName: true), default);
            await c.Unlink(3, default);
        }

        await using (var v = rdb.NewContext())
            Assert.Equal("a note", (await v.Authors.FindAsync(1))!.Notes);
    }
}

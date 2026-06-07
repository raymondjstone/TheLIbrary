using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using TheLibrary.Server.Controllers;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

public class DamagedControllerTests
{
    [Fact]
    public async Task GetDamaged_Excludes_Archived_And_Groups_Bad_Copies_By_Book()
    {
        var dbName = NewDb();
        await using (var db = CreateDb(dbName))
        {
            var author = new Author { Name = "A" };
            var book = new Book { Title = "Grouped Book", Author = author };
            // Two bad copies of the same book + one already-archived (excluded).
            db.LocalBookFiles.AddRange(
                Linked("/lib/A/copy.epub", book),
                Linked("/lib/A/copy.pdf", book),
                Linked("/lib/__archive/A/old.epub", book));
            await db.SaveChangesAsync();
        }

        await using var verify = CreateDb(dbName);
        var controller = new DamagedController(verify, null!, new FakeFileSystem(), new FakeLifetime());

        var groups = await controller.GetDamaged();

        var group = Assert.Single(groups);                  // one book
        Assert.Equal("Grouped Book", group.Title);
        Assert.Equal(2, group.Files.Count);                 // archived copy excluded
        Assert.DoesNotContain(group.Files, f => f.Path.Contains("__archive"));
    }

    [Fact]
    public async Task GetDamaged_Reports_Author_Priority_For_Starred_Filtering()
    {
        var dbName = NewDb();
        await using (var db = CreateDb(dbName))
        {
            var starred = new Author { Name = "Starred", Priority = 3 };
            var plain = new Author { Name = "Plain", Priority = 0 };
            db.LocalBookFiles.AddRange(
                Linked("/lib/S/a.epub", new Book { Title = "By Starred", Author = starred }),
                Linked("/lib/P/b.epub", new Book { Title = "By Plain", Author = plain }));
            await db.SaveChangesAsync();
        }

        await using var verify = CreateDb(dbName);
        var controller = new DamagedController(verify, null!, new FakeFileSystem(), new FakeLifetime());

        var groups = await controller.GetDamaged();

        Assert.Equal(3, groups.Single(g => g.Title == "By Starred").AuthorPriority);
        Assert.Equal(0, groups.Single(g => g.Title == "By Plain").AuthorPriority);
    }

    [Fact]
    public async Task MarkOk_Clears_Flag_And_Keeps_Fingerprint_Current()
    {
        var dbName = NewDb();
        int id;
        var mtime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await using (var db = CreateDb(dbName))
        {
            var f = Damaged("/lib/Author/book.epub");
            f.SizeBytes = 1234;
            f.ModifiedAt = mtime;
            db.LocalBookFiles.Add(f);
            await db.SaveChangesAsync();
            id = f.Id;
        }

        await using var db2 = CreateDb(dbName);
        var controller = new DamagedController(db2, null!, new FakeFileSystem(), new FakeLifetime());

        var result = await controller.MarkOk(id, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        await using var verify = CreateDb(dbName);
        var file = await verify.LocalBookFiles.SingleAsync();
        Assert.True(file.IntegrityOk);
        Assert.Null(file.IntegrityError);
        // Both size and mtime stamped → won't be re-checked until one changes.
        Assert.Equal(1234, file.IntegrityCheckedSize);
        Assert.Equal(mtime, file.IntegrityCheckedModified);
    }

    [Fact]
    public async Task Archive_Moves_File_To_Archive_Folder_And_Updates_Path()
    {
        var dbName = NewDb();
        int id;
        await using (var db = CreateDb(dbName))
        {
            db.LibraryLocations.Add(new LibraryLocation { Path = "/lib", Enabled = true });
            var f = Damaged("/lib/Author/book.epub");
            db.LocalBookFiles.Add(f);
            await db.SaveChangesAsync();
            id = f.Id;
        }

        var fs = new FakeFileSystem();
        fs.AddFile("/lib/Author/book.epub", new byte[] { 1, 2, 3 });

        await using var db2 = CreateDb(dbName);
        var controller = new DamagedController(db2, null!, fs, new FakeLifetime());

        var action = await controller.Archive(id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var payload = Assert.IsType<DamagedController.ArchiveResult>(ok.Value);
        Assert.True(payload.Archived);
        Assert.False(fs.FileExists("/lib/Author/book.epub")); // moved off original path

        await using var verify = CreateDb(dbName);
        var file = await verify.LocalBookFiles.SingleAsync();
        Assert.Contains("__archive", file.FullPath);
        Assert.True(fs.FileExists(file.FullPath));
    }

    [Fact]
    public async Task Archive_Rejects_File_Outside_Library_Roots()
    {
        var dbName = NewDb();
        int id;
        await using (var db = CreateDb(dbName))
        {
            db.LibraryLocations.Add(new LibraryLocation { Path = "/lib", Enabled = true });
            var f = Damaged("/elsewhere/book.epub");
            db.LocalBookFiles.Add(f);
            await db.SaveChangesAsync();
            id = f.Id;
        }

        await using var db2 = CreateDb(dbName);
        var controller = new DamagedController(db2, null!, new FakeFileSystem(), new FakeLifetime());

        var action = await controller.Archive(id, CancellationToken.None);

        // Reported as a non-fatal warning (Archived=false), same as the bulk path.
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var payload = Assert.IsType<DamagedController.ArchiveResult>(ok.Value);
        Assert.False(payload.Archived);
        Assert.Contains("outside", payload.Warning!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetAlternates_Lists_Same_Book_Files_Including_Archived()
    {
        var dbName = NewDb();
        int damagedId;
        await using (var db = CreateDb(dbName))
        {
            var author = new Author { Name = "A" };
            var book = new Book { Title = "B", Author = author };
            var other = new Book { Title = "Other", Author = author };
            var damaged = Linked("/lib/Author/bad.epub", book);
            db.LocalBookFiles.AddRange(
                damaged,
                Linked("/lib/__archive/Author/good.epub", book),  // archived copy of same book
                Linked("/lib/Author/unrelated.epub", other));     // different book
            await db.SaveChangesAsync();
            damagedId = damaged.Id;
        }

        await using var db2 = CreateDb(dbName);
        var controller = new DamagedController(db2, null!, new FakeFileSystem(), new FakeLifetime());

        var action = await controller.GetAlternates(damagedId, CancellationToken.None);
        var list = action.Value!;

        var only = Assert.Single(list);
        Assert.Equal("/lib/__archive/Author/good.epub", only.Path);
        Assert.True(only.Archived);
    }

    [Fact]
    public async Task Remove_Deletes_File_And_Row()
    {
        var dbName = NewDb();
        int id;
        await using (var db = CreateDb(dbName))
        {
            var f = Damaged("/lib/Author/bad.epub");
            db.LocalBookFiles.Add(f);
            await db.SaveChangesAsync();
            id = f.Id;
        }
        var fs = new FakeFileSystem();
        fs.AddFile("/lib/Author/bad.epub", new byte[] { 1 });

        await using var db2 = CreateDb(dbName);
        var controller = new DamagedController(db2, null!, fs, new FakeLifetime());

        Assert.IsType<OkObjectResult>(await controller.Remove(id, CancellationToken.None));
        Assert.False(fs.FileExists("/lib/Author/bad.epub"));
        await using var verify = CreateDb(dbName);
        Assert.Empty(await verify.LocalBookFiles.ToListAsync());
    }

    [Fact]
    public async Task ReplaceWith_Restores_Copy_And_Removes_Damaged()
    {
        var dbName = NewDb();
        int damagedId, goodId;
        await using (var db = CreateDb(dbName))
        {
            var author = new Author { Name = "A" };
            var book = new Book { Title = "B", Author = author };
            var damaged = Linked("/lib/Author/bad.epub", book);
            var good = Linked("/lib/__archive/Author/good.epub", book);
            good.IntegrityOk = null;
            db.LocalBookFiles.AddRange(damaged, good);
            await db.SaveChangesAsync();
            damagedId = damaged.Id; goodId = good.Id;
        }
        var fs = new FakeFileSystem();
        fs.AddFile("/lib/Author/bad.epub", new byte[] { 1 });
        fs.AddFile("/lib/__archive/Author/good.epub", new byte[] { 2, 2, 2 });

        await using var db2 = CreateDb(dbName);
        var controller = new DamagedController(db2, null!, fs, new FakeLifetime());

        Assert.IsType<OkObjectResult>(await controller.ReplaceWith(damagedId, goodId, CancellationToken.None));

        Assert.False(fs.FileExists("/lib/Author/bad.epub"));            // damaged deleted
        Assert.False(fs.FileExists("/lib/__archive/Author/good.epub")); // good moved out of archive
        await using var verify = CreateDb(dbName);
        var remaining = await verify.LocalBookFiles.SingleAsync();      // only the good copy left
        Assert.Equal(goodId, remaining.Id);
        Assert.DoesNotContain("__archive", remaining.FullPath);
        Assert.Contains("Author", remaining.FullPath);
        Assert.Null(remaining.IntegrityCheckedSize);                    // re-queued for a fresh check
        Assert.True(fs.FileExists(remaining.FullPath));
    }

    [Fact]
    public async Task ArchiveReplaced_Archives_Damaged_With_Good_Preferred_Copy_Only()
    {
        var dbName = NewDb();
        await using (var db = CreateDb(dbName))
        {
            db.LibraryLocations.Add(new LibraryLocation { Path = "/lib", Enabled = true });
            var author = new Author { Name = "A" };

            // Book 1: damaged pdf + healthy epub (epub is a replacement format) → archive the pdf.
            var b1 = new Book { Title = "Has good copy", Author = author };
            var damagedWithGood = Linked("/lib/A/has-good.pdf", b1);
            var goodEpub = Good("/lib/A/has-good.epub", b1);

            // Book 2: damaged pdf + only a healthy txt (NOT a replacement format) → keep it.
            var b2 = new Book { Title = "No good copy", Author = author };
            var damagedNoGood = Linked("/lib/A/no-good.pdf", b2);
            var goodTxt = Good("/lib/A/no-good.txt", b2);

            db.LocalBookFiles.AddRange(damagedWithGood, goodEpub, damagedNoGood, goodTxt);
            await db.SaveChangesAsync();
        }

        var fs = new FakeFileSystem();
        fs.AddFile("/lib/A/has-good.pdf", new byte[] { 1 });
        fs.AddFile("/lib/A/has-good.epub", new byte[] { 2 });
        fs.AddFile("/lib/A/no-good.pdf", new byte[] { 3 });
        fs.AddFile("/lib/A/no-good.txt", new byte[] { 4 });

        await using var db2 = CreateDb(dbName);
        var controller = new DamagedController(db2, null!, fs, new FakeLifetime());

        var action = await controller.ArchiveReplaced(CancellationToken.None);
        var result = Assert.IsType<DamagedController.ArchiveReplacedResult>(((OkObjectResult)action.Result!).Value);

        Assert.Equal(1, result.Archived);
        Assert.Equal(1, result.Skipped);

        await using var verify = CreateDb(dbName);
        var hasGood = await verify.LocalBookFiles.SingleAsync(f => f.IntegrityOk == false && f.FullPath.Contains("has-good"));
        var noGood = await verify.LocalBookFiles.SingleAsync(f => f.IntegrityOk == false && f.FullPath.Contains("no-good"));
        Assert.Contains("__archive", hasGood.FullPath);     // archived
        Assert.DoesNotContain("__archive", noGood.FullPath); // left alone
    }

    [Fact]
    public async Task ArchiveFiles_Archives_All_Bad_Copies_Of_A_Book()
    {
        var dbName = NewDb();
        var ids = new List<int>();
        await using (var db = CreateDb(dbName))
        {
            db.LibraryLocations.Add(new LibraryLocation { Path = "/lib", Enabled = true });
            var author = new Author { Name = "A" };
            var book = new Book { Title = "B", Author = author };
            var f1 = Linked("/lib/A/copy.epub", book);
            var f2 = Linked("/lib/A/copy.pdf", book);
            db.LocalBookFiles.AddRange(f1, f2);
            await db.SaveChangesAsync();
            ids.Add(f1.Id); ids.Add(f2.Id);
        }
        var fs = new FakeFileSystem();
        fs.AddFile("/lib/A/copy.epub", new byte[] { 1 });
        fs.AddFile("/lib/A/copy.pdf", new byte[] { 2 });

        await using var db2 = CreateDb(dbName);
        var controller = new DamagedController(db2, null!, fs, new FakeLifetime());

        var action = await controller.ArchiveFiles(new DamagedController.ArchiveFilesRequest(ids), CancellationToken.None);
        var result = Assert.IsType<DamagedController.ArchiveReplacedResult>(((OkObjectResult)action.Result!).Value);

        Assert.Equal(2, result.Archived);
        await using var verify = CreateDb(dbName);
        Assert.All(await verify.LocalBookFiles.ToListAsync(), f => Assert.Contains("__archive", f.FullPath));
        Assert.False(fs.FileExists("/lib/A/copy.epub"));
        Assert.False(fs.FileExists("/lib/A/copy.pdf"));
    }

    private static LocalBookFile Good(string path, Book book) => new()
    {
        FullPath = path,
        AuthorFolder = "A",
        TitleFolder = "Title",
        Book = book,
        IntegrityOk = true,
    };

    private static LocalBookFile Linked(string path, Book book) => new()
    {
        FullPath = path,
        AuthorFolder = "Author",
        TitleFolder = "Title",
        Book = book,
        IntegrityOk = false,
        IntegrityCheckedAt = DateTime.UtcNow,
    };

    private static LocalBookFile Damaged(string path) => new()
    {
        FullPath = path,
        AuthorFolder = "Author",
        TitleFolder = "Title",
        IntegrityOk = false,
        IntegrityError = "EPUB has about 3 page(s) of text; at least 20 required.",
        IntegrityPages = 3,
        IntegrityCheckedAt = DateTime.UtcNow,
    };

    private static string NewDb() => $"damaged-tests-{Guid.NewGuid():N}";
    private static LibraryDbContext CreateDb(string name)
        => new(new DbContextOptionsBuilder<LibraryDbContext>().UseInMemoryDatabase(name).Options);

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}

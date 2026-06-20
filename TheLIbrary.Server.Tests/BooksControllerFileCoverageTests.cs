using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Controllers;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.IO;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

// Filesystem-backed coverage for BooksController's duplicate/file-candidate/link
// endpoints (real temp files + SystemFileSystem + relational SQLite).
public sealed class BooksControllerFileCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tl-bookfile-" + Guid.NewGuid().ToString("N"));
    private readonly string _authorDir;

    public BooksControllerFileCoverageTests()
    {
        _authorDir = Path.Combine(_root, "Auth");
        Directory.CreateDirectory(_authorDir);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static BooksController NewController(LibraryDbContext db)
        => new(db, httpFactory: null!, new SystemFileSystem());

    [Fact]
    public async Task Duplicates_Lists_A_Two_File_Book()
    {
        using var rdb = new RelationalTestDb();
        var epub = Path.Combine(_authorDir, "book.epub");
        var pdf = Path.Combine(_authorDir, "book.pdf");
        await File.WriteAllTextAsync(epub, "x");
        await File.WriteAllTextAsync(pdf, "x");

        await using (var s = rdb.NewContext())
        {
            s.LibraryLocations.Add(new LibraryLocation { Id = 1, Label = "L", Path = _root, Enabled = true, IsPrimary = true, CreatedAt = DateTime.UtcNow });
            s.Authors.Add(new Author { Id = 1, Name = "Auth", Priority = 1, CalibreFolderName = "Auth" });
            s.Books.Add(new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W", Title = "Book", NormalizedTitle = "book" });
            s.LocalBookFiles.AddRange(
                new LocalBookFile { Id = 1, BookId = 10, AuthorId = 1, AuthorFolder = "Auth", FullPath = epub, ModifiedAt = DateTime.UtcNow },
                new LocalBookFile { Id = 2, BookId = 10, AuthorId = 1, AuthorFolder = "Auth", FullPath = pdf, ModifiedAt = DateTime.UtcNow });
            await s.SaveChangesAsync();
        }

        await using var db = rdb.NewContext();
        var groups = await NewController(db).Duplicates(default);
        Assert.Contains(groups, g => g.BookId == 10 && g.Files.Count == 2);
    }

    [Fact]
    public async Task FileCandidates_And_LinkFile_By_Path()
    {
        using var rdb = new RelationalTestDb();
        // An unmatched library file the candidate search can find for the book.
        var loose = Path.Combine(_authorDir, "The Target Title.epub");
        await File.WriteAllTextAsync(loose, "x");

        await using (var s = rdb.NewContext())
        {
            s.LibraryLocations.Add(new LibraryLocation { Id = 1, Label = "L", Path = _root, Enabled = true, IsPrimary = true, CreatedAt = DateTime.UtcNow });
            s.Authors.Add(new Author { Id = 1, Name = "Auth", Priority = 1, CalibreFolderName = "Auth" });
            s.Books.Add(new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W", Title = "The Target Title", NormalizedTitle = "the target title" });
            s.LocalBookFiles.Add(new LocalBookFile { Id = 1, BookId = null, AuthorId = 1, AuthorFolder = "Auth", TitleFolder = "The Target Title", FullPath = loose, ModifiedAt = DateTime.UtcNow });
            await s.SaveChangesAsync();
        }

        await using (var db = rdb.NewContext())
        {
            var candidates = await NewController(db).GetFileCandidates(10, default);
            Assert.NotNull(candidates);
        }

        await using (var db = rdb.NewContext())
        {
            var linked = await NewController(db).LinkFile(10,
                new BooksController.LinkFileRequest(FileId: 1, FilePath: null, Move: false), default);
            Assert.NotNull(linked);
        }

        // The book is now owned (file linked).
        await using (var v = rdb.NewContext())
            Assert.True(await v.LocalBookFiles.AnyAsync(f => f.BookId == 10));
    }
}

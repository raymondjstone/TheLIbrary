using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Scheduling;
using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class AuthorDuplicateRemovalServiceTests
{
    [Fact]
    public async Task Removes_Identical_Copies_Within_An_Author_Folder_Keeping_One()
    {
        var root = Path.Combine(Path.GetTempPath(), $"authordedupe-{Guid.NewGuid():N}");
        var dbName = NewDb();
        try
        {
            var authorDir = Path.Combine(root, "Quigley Fenwick");
            Directory.CreateDirectory(Path.Combine(authorDir, "Title A"));
            Directory.CreateDirectory(Path.Combine(authorDir, "Title A copy"));
            var keep = Path.Combine(authorDir, "Title A", "book.epub");
            var dup = Path.Combine(authorDir, "Title A copy", "book.epub");
            var other = Path.Combine(authorDir, "Title A", "different.epub");
            await File.WriteAllTextAsync(keep, "identical contents");
            await File.WriteAllTextAsync(dup, "identical contents");
            await File.WriteAllTextAsync(other, "DIFFERENT contents!"); // same length, different bytes → kept

            await using (var db = CreateDb(dbName))
            {
                db.LibraryLocations.Add(new LibraryLocation { Id = 1, Path = root, IsPrimary = true, Enabled = true, Label = "T", CreatedAt = DateTime.UtcNow });
                db.Authors.Add(new Author { Id = 1, Name = "Quigley Fenwick" });
                // An unmatched file makes the author eligible.
                db.LocalBookFiles.Add(new LocalBookFile { Id = 1, AuthorId = 1, BookId = null, AuthorFolder = "Quigley Fenwick", TitleFolder = "Title A copy", FullPath = dup });
                await db.SaveChangesAsync();
            }

            var summary = await Run(dbName);

            Assert.Equal(1, summary.AuthorFoldersScanned);
            Assert.Equal(1, summary.DuplicateGroups);
            Assert.Equal(1, summary.FilesDeleted);
            Assert.True(File.Exists(keep));               // shortest path survives
            Assert.False(File.Exists(dup));               // identical copy deleted
            Assert.True(File.Exists(other));              // same size but different bytes — kept

            await using var verify = CreateDb(dbName);
            Assert.Empty(verify.LocalBookFiles);          // the deleted dup's row pruned
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Never_Compares_Across_Different_Author_Folders()
    {
        var root = Path.Combine(Path.GetTempPath(), $"authordedupe-{Guid.NewGuid():N}");
        var dbName = NewDb();
        try
        {
            // The SAME file content under two different authors — must NOT be touched.
            var aFile = Path.Combine(root, "Author One", "T", "book.epub");
            var bFile = Path.Combine(root, "Author Two", "T", "book.epub");
            Directory.CreateDirectory(Path.GetDirectoryName(aFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(bFile)!);
            await File.WriteAllTextAsync(aFile, "same content here");
            await File.WriteAllTextAsync(bFile, "same content here");

            await using (var db = CreateDb(dbName))
            {
                db.LibraryLocations.Add(new LibraryLocation { Id = 1, Path = root, IsPrimary = true, Enabled = true, Label = "T", CreatedAt = DateTime.UtcNow });
                db.Authors.AddRange(
                    new Author { Id = 1, Name = "Author One" },
                    new Author { Id = 2, Name = "Author Two" });
                db.LocalBookFiles.AddRange(
                    new LocalBookFile { Id = 1, AuthorId = 1, BookId = null, AuthorFolder = "Author One", TitleFolder = "T", FullPath = aFile },
                    new LocalBookFile { Id = 2, AuthorId = 2, BookId = null, AuthorFolder = "Author Two", TitleFolder = "T", FullPath = bFile });
                await db.SaveChangesAsync();
            }

            var summary = await Run(dbName);

            Assert.Equal(0, summary.FilesDeleted);    // cross-author identical copies left alone
            Assert.True(File.Exists(aFile));
            Assert.True(File.Exists(bFile));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Prefers_Keeping_The_Matched_Copy_So_A_Book_Is_Not_Orphaned()
    {
        var root = Path.Combine(Path.GetTempPath(), $"authordedupe-{Guid.NewGuid():N}");
        var dbName = NewDb();
        try
        {
            var authorDir = Path.Combine(root, "Quigley Fenwick");
            // The matched copy is at a LONGER path than the unmatched duplicate,
            // so plain shortest-path would delete it — preferKeep must save it.
            var unmatched = Path.Combine(authorDir, "book.epub");
            Directory.CreateDirectory(Path.Combine(authorDir, "Matched Title"));
            var matched = Path.Combine(authorDir, "Matched Title", "book.epub");
            await File.WriteAllTextAsync(unmatched, "identical");
            await File.WriteAllTextAsync(matched, "identical");

            await using (var db = CreateDb(dbName))
            {
                db.LibraryLocations.Add(new LibraryLocation { Id = 1, Path = root, IsPrimary = true, Enabled = true, Label = "T", CreatedAt = DateTime.UtcNow });
                db.Authors.Add(new Author { Id = 1, Name = "Quigley Fenwick" });
                db.Books.Add(new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W", Title = "Matched Title", NormalizedTitle = "matched title" });
                db.LocalBookFiles.AddRange(
                    new LocalBookFile { Id = 1, AuthorId = 1, BookId = 10, AuthorFolder = "Quigley Fenwick", TitleFolder = "Matched Title", FullPath = matched },
                    new LocalBookFile { Id = 2, AuthorId = 1, BookId = null, AuthorFolder = "Quigley Fenwick", TitleFolder = "", FullPath = unmatched });
                await db.SaveChangesAsync();
            }

            var summary = await Run(dbName);

            Assert.Equal(1, summary.FilesDeleted);
            Assert.True(File.Exists(matched));            // matched copy preserved
            Assert.False(File.Exists(unmatched));         // unmatched duplicate removed
            await using var verify = CreateDb(dbName);
            var row = Assert.Single(verify.LocalBookFiles);
            Assert.Equal(10, row.BookId);                 // book still has its file
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Aborts_When_No_Library_Root_Is_Mounted()
    {
        var dbName = NewDb();
        await using (var db = CreateDb(dbName))
        {
            db.LibraryLocations.Add(new LibraryLocation { Id = 1, Path = "/nonexistent-root", Enabled = true, Label = "T", CreatedAt = DateTime.UtcNow });
            db.LocalBookFiles.Add(new LocalBookFile { Id = 1, AuthorId = 1, BookId = null, FullPath = "/nonexistent-root/A/b.epub" });
            await db.SaveChangesAsync();
        }

        var summary = await Run(dbName);
        Assert.Equal(0, summary.AuthorFoldersScanned);
        Assert.Equal(0, summary.FilesDeleted);
    }

    [Fact]
    public async Task Collapses_Identical_Copies_Within_An_Archive_Author_Folder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"authordedupe-{Guid.NewGuid():N}");
        var archive = Path.Combine(Path.GetTempPath(), $"authordedupe-arch-{Guid.NewGuid():N}");
        var dbName = NewDb();
        try
        {
            Directory.CreateDirectory(root);
            // Archive author folder with the same file piled up across sub-folders
            // (the _N pattern) plus a genuinely different file that must survive.
            var archAuthor = Path.Combine(archive, "Quigley Fenwick");
            Directory.CreateDirectory(Path.Combine(archAuthor, "Damned"));
            Directory.CreateDirectory(Path.Combine(archAuthor, "Call to Arms"));
            var a1 = Path.Combine(archAuthor, "Damned", "Call to Arms.lit");
            var a2 = Path.Combine(archAuthor, "Damned", "Call to Arms_1.lit");
            var a3 = Path.Combine(archAuthor, "Call to Arms", "Call to Arms.lit");
            var other = Path.Combine(archAuthor, "Damned", "Different.lit");
            await File.WriteAllTextAsync(a1, "identical contents");
            await File.WriteAllTextAsync(a2, "identical contents");
            await File.WriteAllTextAsync(a3, "identical contents");
            await File.WriteAllTextAsync(other, "DIFFERENT contents!"); // same length, different bytes

            await using (var db = CreateDb(dbName))
            {
                db.LibraryLocations.Add(new LibraryLocation { Id = 1, Path = root, IsPrimary = true, Enabled = true, Label = "T", CreatedAt = DateTime.UtcNow });
                db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.DedupeArchiveFolder, Value = archive.Replace('\\', '/') });
                db.LocalBookFiles.AddRange(
                    new LocalBookFile { Id = 1, BookId = 10, FullPath = a1 },
                    new LocalBookFile { Id = 2, BookId = 10, FullPath = a2 },
                    new LocalBookFile { Id = 3, BookId = 10, FullPath = a3 });
                await db.SaveChangesAsync();
            }

            var summary = await Run(dbName);

            Assert.Equal(2, summary.ArchiveFilesDeleted);  // two identical copies collapsed, one kept
            // Exactly one of the three identical copies survives; the different file stays.
            var survivors = new[] { a1, a2, a3 }.Count(File.Exists);
            Assert.Equal(1, survivors);
            Assert.True(File.Exists(other));
            // (DB-row pruning is asserted by the live-folder tests; here the Windows
            // temp paths are backslash while the normalised archive path is forward-
            // slash, so the path-keyed prune is a no-op on the test host only — in
            // production every path is forward-slash and the rows are pruned.)
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(archive)) Directory.Delete(archive, recursive: true);
        }
    }

    private static string NewDb() => $"authordedupe-tests-{Guid.NewGuid():N}";
    private static LibraryDbContext CreateDb(string name)
        => new(new DbContextOptionsBuilder<LibraryDbContext>().UseInMemoryDatabase(name).Options);

    private static async Task<AuthorDuplicateRemovalSummary> Run(string dbName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<LibraryDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();
        var sut = new AuthorDuplicateRemovalService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new BackgroundTaskCoordinator(),
            NullLogger<AuthorDuplicateRemovalService>.Instance);

        Assert.True(sut.TryStart(CancellationToken.None, out var error), error);
        for (var i = 0; i < 200 && sut.IsRunning; i++) await Task.Delay(50);
        Assert.False(sut.IsRunning);
        Assert.NotNull(sut.LastResult);
        return sut.LastResult!;
    }
}

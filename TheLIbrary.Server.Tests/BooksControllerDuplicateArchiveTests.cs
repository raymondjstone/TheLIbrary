using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Controllers;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

public class BooksControllerDuplicateArchiveTests
{
    // Regression: when the archive folder is a different mount, File.Move is
    // copy+delete and the source delete can silently fail, leaving the live
    // original. The archive action must force-remove the surviving source and
    // only then repoint the row — otherwise the original reappears as a duplicate
    // forever (the root cause of the recurring archived duplicates).
    [Fact]
    public async Task Archive_Force_Removes_Source_When_Move_Leaves_It_Behind()
    {
        var fs = new FakeFileSystem();
        fs.CreateDirectory("/Books/Collection");
        const string keep = "/Books/Collection/Foster/Damned/Call to Arms.epub";
        const string extra = "/Books/Collection/Foster/Call to Arms/Call to Arms.lit";
        fs.ExistingFiles.Add(keep);
        fs.ExistingFiles.Add(extra);
        // Simulate the cross-mount move leaving the source in place.
        fs.MoveLeavesSource.Add(extra);

        await using var db = CreateDb();
        db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.DedupeArchiveFolder, Value = "/Books/TheLibrary_Archive" });
        db.LibraryLocations.Add(new LibraryLocation { Id = 1, Path = "/Books/Collection", IsPrimary = true, Enabled = true, Label = "Default", CreatedAt = DateTime.UtcNow });
        db.Authors.Add(new Author { Id = 1, Name = "Foster" });
        db.Books.Add(new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W", Title = "Call to Arms", NormalizedTitle = "call to arms" });
        db.LocalBookFiles.AddRange(
            new LocalBookFile { Id = 1, BookId = 10, AuthorId = 1, FullPath = keep },
            new LocalBookFile { Id = 2, BookId = 10, AuthorId = 1, FullPath = extra });
        await db.SaveChangesAsync();

        var sut = new BooksController(db, httpFactory: null!, fs);
        var result = await sut.ApplyDuplicateAction(
            new BooksController.DuplicateActionRequest(new[] { 2 }, "archive", null), CancellationToken.None);

        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result.Result);
        var body = Assert.IsType<BooksController.DuplicateActionResult>(ok.Value);
        Assert.Equal(1, body.Archived);

        // The live original must be gone (force-removed despite the move leaving it).
        Assert.False(fs.FileExists(extra));

        // The row was repointed into the archive, and the archived copy exists there.
        // The stored path MUST be forward-slash with no backslashes, even on the
        // Windows test host: the Duplicates list excludes archived rows by matching
        // the forward-slash archive prefix, so a Path.Combine '\' would make the
        // archived copy silently fail that exclusion and keep showing as a duplicate.
        var row = await db.LocalBookFiles.FirstAsync(f => f.Id == 2);
        Assert.DoesNotContain('\\', row.FullPath);
        Assert.StartsWith("/Books/TheLibrary_Archive/", row.FullPath);
        Assert.True(fs.FileExists(row.FullPath));
    }

    // End-to-end of the user-visible symptom: a book has a keeper plus an extra in a
    // differently-named folder; archiving the extra must make the duplicate group
    // DISAPPEAR from the Duplicates list. The list excludes rows under the archive
    // folder by a forward-slash match, so the archive write has to store a
    // forward-slash path — the regression stored '\' on Windows and the group stayed.
    [Fact]
    public async Task Archiving_An_Extra_Removes_The_Group_From_The_Duplicates_List()
    {
        var fs = new FakeFileSystem();
        const string keep = "/Books/Collection/Foster_OL1A/Series/Triumph of Souls (epub).epub";
        const string extra = "/Books/Collection/Foster_OL1A/Triumph of Souls/Triumph of Souls.pdf";
        fs.ExistingFiles.Add(keep);
        fs.ExistingFiles.Add(extra);

        await using var db = CreateDb();
        db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.DedupeArchiveFolder, Value = "/Books/TheLibrary_Archive" });
        db.LibraryLocations.Add(new LibraryLocation { Id = 1, Path = "/Books/Collection", IsPrimary = true, Enabled = true, Label = "Default", CreatedAt = DateTime.UtcNow });
        db.Authors.Add(new Author { Id = 1, Name = "Foster", Priority = 1 });
        db.Books.Add(new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W", Title = "Triumph of Souls", NormalizedTitle = "triumph of souls" });
        db.LocalBookFiles.AddRange(
            new LocalBookFile { Id = 1, BookId = 10, AuthorId = 1, FullPath = keep },
            new LocalBookFile { Id = 2, BookId = 10, AuthorId = 1, FullPath = extra });
        await db.SaveChangesAsync();

        var sut = new BooksController(db, httpFactory: null!, fs);

        // Before: one duplicate group with two files.
        var before = await sut.Duplicates(CancellationToken.None);
        Assert.Single(before);
        Assert.Equal(2, before[0].Files.Count);

        await sut.ApplyDuplicateAction(
            new BooksController.DuplicateActionRequest(new[] { 2 }, "archive", null), CancellationToken.None);

        // After: the archived row is excluded, so the keeper stands alone and the
        // group is gone from the list entirely.
        var after = await sut.Duplicates(CancellationToken.None);
        Assert.Empty(after);
    }

    private static LibraryDbContext CreateDb()
        => new(new DbContextOptionsBuilder<LibraryDbContext>()
            .UseInMemoryDatabase($"dedupe-archive-{Guid.NewGuid():N}").Options);
}

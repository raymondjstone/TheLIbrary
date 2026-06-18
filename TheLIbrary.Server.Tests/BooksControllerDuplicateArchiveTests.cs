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
        // (Separator-agnostic: Path.Combine uses '\' on the Windows test host, '/' on
        // the Linux production mount.)
        var row = await db.LocalBookFiles.FirstAsync(f => f.Id == 2);
        Assert.StartsWith("/Books/TheLibrary_Archive", row.FullPath.Replace('\\', '/'));
        Assert.True(fs.FileExists(row.FullPath));
    }

    private static LibraryDbContext CreateDb()
        => new(new DbContextOptionsBuilder<LibraryDbContext>()
            .UseInMemoryDatabase($"dedupe-archive-{Guid.NewGuid():N}").Options);
}

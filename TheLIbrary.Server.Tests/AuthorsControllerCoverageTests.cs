using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Controllers;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

// Endpoint coverage for AuthorsController (the list/detail/query surface and the
// simple per-author mutations) against a real relational DB. Heavy collaborators
// the tested endpoints don't touch (OpenLibrary client, refresher, converter,
// content-scan, assigner, lifetime) are passed as null — only _db and _fs are
// exercised here.
public class AuthorsControllerCoverageTests
{
    private static AuthorsController NewController(LibraryDbContext db)
        => new(db, ol: null!, refresher: null!, manualBooks: null!, manualAuthors: null!, fs: new FakeFileSystem(),
            log: NullLogger<AuthorsController>.Instance, converter: null!, contentScan: null!,
            assigner: null!, lifetime: null!);

    private static async Task SeedAsync(RelationalTestDb rdb)
    {
        await using var s = rdb.NewContext();
        s.Authors.AddRange(
            new Author { Id = 1, Name = "Primary Auth", Priority = 2, Status = AuthorStatus.Active, CalibreFolderName = "Primary Auth" },
            new Author { Id = 2, Name = "Second Auth", Priority = 0, Status = AuthorStatus.Active },
            new Author { Id = 3, Name = "Merge Source", Priority = 0, Status = AuthorStatus.Active });
        s.Books.AddRange(
            new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W", Title = "Alpha", NormalizedTitle = "alpha", FirstPublishYear = 2001, ManuallyOwned = true },
            new Book { Id = 11, AuthorId = 1, OpenLibraryWorkKey = "OL11W", Title = "Beta", NormalizedTitle = "beta", FirstPublishYear = 2002 });
        await s.SaveChangesAsync();
    }

    [Fact]
    public async Task List_And_Query_Endpoints()
    {
        using var rdb = new RelationalTestDb();
        await SeedAsync(rdb);
        await using var db = rdb.NewContext();
        var c = NewController(db);

        Assert.NotEmpty(await c.List(default));
        Assert.NotNull(await c.Starred(default));
        Assert.NotNull(await c.GetStalled(default));

        var detail = await c.Get(1, default);
        Assert.NotNull(detail.Value);
        Assert.Equal(2, detail.Value!.Books.Count);
    }

    [Fact]
    public async Task Get_Unknown_Author_Is_NotFound()
    {
        using var rdb = new RelationalTestDb();
        await SeedAsync(rdb);
        await using var db = rdb.NewContext();
        var detail = await NewController(db).Get(999, default);
        Assert.IsType<NotFoundResult>(detail.Result);
    }

    [Fact]
    public async Task Per_Author_Mutations_Persist()
    {
        using var rdb = new RelationalTestDb();
        await SeedAsync(rdb);

        await using (var db = rdb.NewContext())
        {
            var c = NewController(db);
            await c.SetPriority(2, new AuthorsController.SetPriorityRequest(3), default);
            await c.SetRefreshInterval(2, new AuthorsController.SetRefreshIntervalRequest(14), default);
            await c.SetNotifyOnNewBooks(2, new AuthorsController.SetNotifyOnNewBooksRequest(true), default);
            await c.BulkStatus(new AuthorsController.BulkStatusRequest(new[] { 2 }, "Excluded", "test"), default);
        }

        await using (var v = rdb.NewContext())
        {
            var a = await v.Authors.FindAsync(2);
            Assert.Equal(3, a!.Priority);
            Assert.True(a.NotifyOnNewBooks);
            Assert.Equal(AuthorStatus.Excluded, a.Status);
        }
    }

    [Fact]
    public async Task Untracked_Listings_Run_With_No_Locations()
    {
        using var rdb = new RelationalTestDb();
        await SeedAsync(rdb);
        await using var db = rdb.NewContext();
        var c = NewController(db);

        // No LibraryLocations seeded → empty results, but the endpoints execute.
        Assert.NotNull(await c.Unclaimed(default));
        Assert.NotNull(await c.ListUnknownFolders(default));
    }
}

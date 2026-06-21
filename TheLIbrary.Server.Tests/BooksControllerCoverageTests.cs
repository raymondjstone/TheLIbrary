using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Controllers;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

// Broad endpoint coverage for BooksController, exercised against a real relational
// (SQLite) database so ExecuteUpdate/Delete paths run. Seeds a small, shaped
// catalogue (no real book/author names) and drives each query/mutation endpoint.
public class BooksControllerCoverageTests
{
    private static BooksController NewController(LibraryDbContext db)
        => new(db, httpFactory: null!, new FakeFileSystem());

    private static async Task SeedAsync(RelationalTestDb rdb)
    {
        await using var s = rdb.NewContext();
        var year = DateTime.UtcNow.Year;
        s.Authors.Add(new Author { Id = 1, Name = "Primary Auth", Priority = 1, CalibreFolderName = "Primary Auth" });
        s.Authors.Add(new Author { Id = 2, Name = "Second Auth", Priority = 0 });
        s.Series.Add(new Series { Id = 1, Name = "The Cycle", NormalizedName = "the cycle", PrimaryAuthorId = 1 });
        s.Books.AddRange(
            new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W", Title = "Alpha", NormalizedTitle = "alpha", FirstPublishYear = year, Subjects = "Science fiction;Fantasy", SeriesId = 1, SeriesPosition = "1" },
            new Book { Id = 11, AuthorId = 1, OpenLibraryWorkKey = "OL11W", Title = "Beta", NormalizedTitle = "beta", FirstPublishYear = year, Wanted = true, Subjects = "Fantasy" },
            new Book { Id = 12, AuthorId = 1, OpenLibraryWorkKey = "OL12W", Title = "Gamma", NormalizedTitle = "gamma", FirstPublishYear = year - 1, ManuallyOwned = true },
            new Book { Id = 13, AuthorId = 2, OpenLibraryWorkKey = "OL13W", Title = "Delta", NormalizedTitle = "delta", FirstPublishYear = year, Foreign = true, Suppressed = true });
        await s.SaveChangesAsync();
    }

    [Fact]
    public async Task UpNext_Returns_Next_Unread_Owned_Volume_For_Started_Series()
    {
        using var rdb = new RelationalTestDb();
        await using (var s = rdb.NewContext())
        {
            s.Authors.Add(new Author { Id = 1, Name = "Auth", Priority = 1 });
            s.Series.Add(new Series { Id = 1, Name = "Saga", NormalizedName = "saga", PrimaryAuthorId = 1 });
            // Vol 1 read; Vol 2 owned+unread (the "up next"); Vol 3 owned+unread (later).
            s.Books.AddRange(
                new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W", Title = "Saga 1", NormalizedTitle = "saga 1", SeriesId = 1, SeriesPosition = "1", ManuallyOwned = true, ReadStatus = ReadStatus.Read },
                new Book { Id = 11, AuthorId = 1, OpenLibraryWorkKey = "OL11W", Title = "Saga 2", NormalizedTitle = "saga 2", SeriesId = 1, SeriesPosition = "2", ManuallyOwned = true, ReadStatus = ReadStatus.Unread },
                new Book { Id = 12, AuthorId = 1, OpenLibraryWorkKey = "OL12W", Title = "Saga 3", NormalizedTitle = "saga 3", SeriesId = 1, SeriesPosition = "3", ManuallyOwned = true, ReadStatus = ReadStatus.Unread });
            await s.SaveChangesAsync();
        }
        await using var db = rdb.NewContext();
        var rows = await NewController(db).UpNext(default);
        var row = Assert.Single(rows);
        Assert.Equal(11, row.BookId); // lowest-position owned-unread in a started series
    }

    [Fact]
    public async Task Query_Endpoints_Return_Seeded_Data()
    {
        using var rdb = new RelationalTestDb();
        await SeedAsync(rdb);
        await using var db = rdb.NewContext();
        var c = NewController(db);

        Assert.NotNull(await c.MissingWorks(default));
        Assert.NotNull(await c.PhysicalOnly(default));
        Assert.NotNull(await c.GetWanted(default));
        Assert.NotNull(await c.AllSeries(default));
        Assert.NotNull(await c.Genres(default));
        Assert.NotNull(await c.Foreign(default));
        Assert.NotNull(await c.Manual(default));
        Assert.NotEmpty(await c.RecentReleases(default));
        Assert.NotNull(await c.RecentReleasesAll(default));
    }

    [Fact]
    public async Task PhysicalOnly_Lists_Manual_Without_File()
    {
        using var rdb = new RelationalTestDb();
        await SeedAsync(rdb);
        await using var db = rdb.NewContext();
        var rows = await NewController(db).PhysicalOnly(default);
        Assert.Contains(rows, r => r.Id == 12);
    }

    [Fact]
    public async Task Mutation_Endpoints_Persist()
    {
        using var rdb = new RelationalTestDb();
        await SeedAsync(rdb);

        await using (var db = rdb.NewContext())
        {
            var c = NewController(db);
            await c.Update(10, new BooksController.UpdateBookRequest("Alpha Edited", DateTime.UtcNow.Year, 1), default);
            await c.SetOwnership(11, new BooksController.OwnershipRequest(true), default);
            await c.SetReadStatus(10, new BooksController.ReadStatusRequest(ReadStatus.Read, DateTime.UtcNow), default);
            await c.SetWanted(10, new BooksController.WantedRequest(true), default);
            await c.SetSuppressed(11, new BooksController.SuppressedRequest(true), default);
            await c.SetSeries(11, new BooksController.SeriesRequest("The Cycle", "2"), default);
            await c.BulkSetOwnership(new BooksController.BulkOwnershipRequest(new[] { 10, 11 }, true), default);
            await c.BulkMarkWanted(new BooksController.BulkMarkWantedRequest(new[] { 12 }, true), default);
        }

        await using (var v = rdb.NewContext())
        {
            var b10 = await v.Books.FindAsync(10);
            Assert.Equal("Alpha Edited", b10!.Title);
            Assert.Equal(ReadStatus.Read, b10.ReadStatus);
            Assert.True((await v.Books.FindAsync(11))!.ManuallyOwned);
        }
    }

    [Fact]
    public async Task Foreign_Flow_And_Confirm()
    {
        using var rdb = new RelationalTestDb();
        await SeedAsync(rdb);

        await using (var db = rdb.NewContext())
        {
            var c = NewController(db);
            await c.SetForeign(10, new BooksController.ForeignRequest(true), default);
            await c.ConfirmForeign(13, new BooksController.ConfirmForeignRequest(true), default);
            await c.ConfirmAllForeign(default);
        }

        await using (var v = rdb.NewContext())
            Assert.True((await v.Books.FindAsync(10))!.Foreign);
    }

    [Fact]
    public async Task Delete_Removes_Book()
    {
        using var rdb = new RelationalTestDb();
        await SeedAsync(rdb);

        await using (var db = rdb.NewContext())
            await NewController(db).Delete(13, default);

        await using (var v = rdb.NewContext())
            Assert.Null(await v.Books.FindAsync(13));
    }

    [Fact]
    public async Task Update_Rejects_Implausible_Year()
    {
        using var rdb = new RelationalTestDb();
        await SeedAsync(rdb);
        await using var db = rdb.NewContext();
        var result = await NewController(db).Update(10,
            new BooksController.UpdateBookRequest(null, DateTime.UtcNow.Year + 50, null), default);
        Assert.IsType<BadRequestObjectResult>(result);
    }
}

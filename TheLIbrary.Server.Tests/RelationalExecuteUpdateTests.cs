using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Controllers;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

// Regression coverage for ExecuteUpdate-based endpoints, which the EF InMemory
// provider can't execute (it throws) — so these paths had no test exercising the
// actual relational write. Runs against a real SQLite database via RelationalTestDb.
public class RelationalExecuteUpdateTests
{
    [Fact]
    public async Task BulkSetOwnership_Persists_Via_ExecuteUpdate()
    {
        using var rdb = new RelationalTestDb();
        await using (var seed = rdb.NewContext())
        {
            seed.Authors.Add(new Author { Id = 1, Name = "Author" });
            seed.Books.AddRange(
                new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL1W", Title = "One", NormalizedTitle = "one" },
                new Book { Id = 11, AuthorId = 1, OpenLibraryWorkKey = "OL2W", Title = "Two", NormalizedTitle = "two" });
            await seed.SaveChangesAsync();
        }

        await using (var ctx = rdb.NewContext())
        {
            var controller = new BooksController(ctx, httpFactory: null!, new FakeFileSystem());
            var result = await controller.BulkSetOwnership(
                new BooksController.BulkOwnershipRequest(new[] { 10, 11 }, true), CancellationToken.None);
            Assert.IsType<Microsoft.AspNetCore.Mvc.NoContentResult>(result);
        }

        // Assert through a fresh context so we read the persisted relational state,
        // not a change-tracker snapshot.
        await using (var verify = rdb.NewContext())
        {
            Assert.True(await verify.Books.AllAsync(b => b.ManuallyOwned));
            Assert.True(await verify.Books.AllAsync(b => b.ManuallyOwnedAt != null));
        }
    }

    [Fact]
    public async Task SetOwnedDifferentEdition_Clears_Wanted_On_Relational_Provider()
    {
        using var rdb = new RelationalTestDb();
        await using (var seed = rdb.NewContext())
        {
            seed.Authors.Add(new Author { Id = 1, Name = "Author" });
            seed.Books.Add(new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL1W", Title = "Wanted", NormalizedTitle = "wanted", Wanted = true });
            await seed.SaveChangesAsync();
        }

        await using (var ctx = rdb.NewContext())
        {
            var controller = new BooksController(ctx, httpFactory: null!, new FakeFileSystem());
            await controller.SetOwnedDifferentEdition(10, new BooksController.OwnershipRequest(true), CancellationToken.None);
        }

        await using (var verify = rdb.NewContext())
        {
            var book = await verify.Books.FindAsync(10);
            Assert.True(book!.OwnedDifferentEdition);
            Assert.False(book.Wanted);
        }
    }
}

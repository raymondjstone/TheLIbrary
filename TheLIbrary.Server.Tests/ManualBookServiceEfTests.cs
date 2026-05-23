using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class ManualBookServiceEfTests
{
    [Fact]
    public async Task CreateAsync_Creates_Series_When_Missing_And_Assigns_Book()
    {
        await using var db = CreateDb();
        db.Authors.Add(new Author { Id = 1, Name = "Author" });
        await db.SaveChangesAsync();

        var svc = new ManualBookService(db);
        var result = await svc.CreateAsync(1, "New Book", 2001, "Series Name", "2", true, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.NotNull(result.Book);
        Assert.Equal("Series Name", await db.Series.Select(s => s.Name).SingleAsync());
        Assert.True(result.Book!.ManuallyOwned);
    }

    [Fact]
    public async Task CreateAsync_Uses_Existing_Series_And_Backfills_PrimaryAuthor()
    {
        await using var db = CreateDb();
        db.Authors.Add(new Author { Id = 1, Name = "Author" });
        db.Series.Add(new Series { Id = 2, Name = "Series", NormalizedName = TitleNormalizer.Normalize("Series") });
        await db.SaveChangesAsync();

        var svc = new ManualBookService(db);
        var result = await svc.CreateAsync(1, "New Book", null, "Series", null, false, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(1, (await db.Series.FindAsync(2))!.PrimaryAuthorId);
    }

    [Fact]
    public async Task CreateAsync_Rejects_Linked_NonPenName_Author()
    {
        await using var db = CreateDb();
        db.Authors.AddRange(
            new Author { Id = 1, Name = "Canonical" },
            new Author { Id = 2, Name = "Child", LinkedToAuthorId = 1, IsPenName = false });
        await db.SaveChangesAsync();

        var svc = new ManualBookService(db);
        var result = await svc.CreateAsync(2, "Book", null, null, null, false, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.False(result.Conflict);
    }

    [Fact]
    public async Task CreateAsync_Allows_PenName_Author()
    {
        await using var db = CreateDb();
        db.Authors.AddRange(
            new Author { Id = 1, Name = "Canonical" },
            new Author { Id = 2, Name = "Pen", LinkedToAuthorId = 1, IsPenName = true });
        await db.SaveChangesAsync();

        var svc = new ManualBookService(db);
        var result = await svc.CreateAsync(2, "Book", null, null, null, false, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.NotNull(result.Book);
    }

    [Fact]
    public async Task CreateAsync_Reports_Conflict_For_Duplicate_Normalized_Title()
    {
        await using var db = CreateDb();
        db.Authors.Add(new Author { Id = 1, Name = "Author" });
        db.Books.Add(new Book { Id = 1, AuthorId = 1, OpenLibraryWorkKey = "OL1W", Title = "Existing Book", NormalizedTitle = TitleNormalizer.Normalize("Existing Book") });
        await db.SaveChangesAsync();

        var svc = new ManualBookService(db);
        var result = await svc.CreateAsync(1, "Existing Book", null, null, null, false, CancellationToken.None);

        Assert.True(result.Conflict);
        Assert.NotNull(result.Error);
    }

    private static LibraryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<LibraryDbContext>()
            .UseInMemoryDatabase($"manual-book-tests-{Guid.NewGuid():N}")
            .Options;
        return new LibraryDbContext(options);
    }
}

using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class AuthorNameValidatorTests
{
    [Fact]
    public async Task Validates_Known_OpenLibrary_Author()
    {
        await using var db = CreateDb();
        AddOlAuthor(db, "Quigley Fenwick");
        await db.SaveChangesAsync();

        Assert.Equal("Quigley Fenwick",
            await AuthorNameValidator.ValidateAsync(db, "Quigley Fenwick", CancellationToken.None));
    }

    [Fact]
    public async Task Returns_Display_Form_For_Last_Comma_First_Input()
    {
        await using var db = CreateDb();
        AddOlAuthor(db, "Quigley Fenwick");
        await db.SaveChangesAsync();

        Assert.Equal("Quigley Fenwick",
            await AuthorNameValidator.ValidateAsync(db, "Fenwick, Quigley", CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_Unknown_And_Blank_Names()
    {
        await using var db = CreateDb();
        await db.SaveChangesAsync();

        Assert.Null(await AuthorNameValidator.ValidateAsync(db, "Totally Madeup Person", CancellationToken.None));
        Assert.Null(await AuthorNameValidator.ValidateAsync(db, "   ", CancellationToken.None));
        Assert.Null(await AuthorNameValidator.ValidateAsync(db, null, CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_Blacklisted_Name_Even_When_In_Catalogue()
    {
        await using var db = CreateDb();
        AddOlAuthor(db, "Quigley Fenwick");
        db.AuthorBlacklist.Add(new AuthorBlacklist
        {
            NormalizedName = TitleNormalizer.NormalizeAuthor("Quigley Fenwick"),
        });
        await db.SaveChangesAsync();

        Assert.Null(await AuthorNameValidator.ValidateAsync(db, "Quigley Fenwick", CancellationToken.None));
    }

    [Fact]
    public async Task Initials_Run_Matches_The_Spaced_Catalogue_Form()
    {
        // "QW Fenwick" normalizes to "qw fenwick", which can never equal the
        // catalogue's "q w fenwick" — the spaced-out candidate bridges that.
        await using var db = CreateDb();
        AddOlAuthor(db, "Q. W. Fenwick");
        await db.SaveChangesAsync();

        Assert.Equal("Q W Fenwick",
            await AuthorNameValidator.ValidateAsync(db, "QW Fenwick", CancellationToken.None));
    }

    [Fact]
    public async Task Validates_Against_Existing_Watchlist_Author_By_Exact_Name()
    {
        await using var db = CreateDb();
        db.Authors.Add(new Author { Name = "Quigley Fenwick" });
        await db.SaveChangesAsync();

        Assert.Equal("Quigley Fenwick",
            await AuthorNameValidator.ValidateAsync(db, "Fenwick, Quigley", CancellationToken.None));
    }

    private static void AddOlAuthor(LibraryDbContext db, string name)
        => db.OpenLibraryAuthors.Add(new OpenLibraryAuthor
        {
            OlKey = $"OL{Math.Abs(name.GetHashCode()) % 10000}A",
            Name = name,
            NormalizedName = TitleNormalizer.NormalizeAuthor(name),
            ImportedAt = DateTime.UtcNow,
        });

    private static LibraryDbContext CreateDb()
        => new(new DbContextOptionsBuilder<LibraryDbContext>()
            .UseInMemoryDatabase($"author-validator-tests-{Guid.NewGuid():N}").Options);
}

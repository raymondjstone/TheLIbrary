using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Controllers;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using Xunit;

namespace TheLibrary.Server.Tests;

public class RecommendationsControllerTests
{
    [Fact]
    public async Task Reject_Removes_Author_From_Future_Suggestions()
    {
        await using var db = CreateDb();

        // Taste profile: one owned book in the "Spacefaring" genre.
        db.Authors.Add(new Author { Id = 1, Name = "Owned Star", Priority = 3, Status = AuthorStatus.Active });
        db.Books.Add(new Book
        {
            Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W",
            Title = "Owned One", NormalizedTitle = "owned one",
            ManuallyOwned = true, Subjects = "Spacefaring",
        });

        // A candidate: unstarred, active, writes the same genre → should be suggested.
        db.Authors.Add(new Author { Id = 2, Name = "Candidate One", Priority = 0, Status = AuthorStatus.Active });
        db.Books.Add(new Book
        {
            Id = 20, AuthorId = 2, OpenLibraryWorkKey = "OL20W",
            Title = "Candidate Book", NormalizedTitle = "candidate book",
            Subjects = "Spacefaring",
        });
        await db.SaveChangesAsync();

        var sut = new RecommendationsController(db);

        var before = await sut.Get(CancellationToken.None);
        Assert.Contains(before, s => s.Id == 2);

        var rejectResult = await sut.Reject(2, CancellationToken.None);
        Assert.IsType<NoContentResult>(rejectResult);

        var after = await sut.Get(CancellationToken.None);
        Assert.DoesNotContain(after, s => s.Id == 2);

        // Reversible: un-rejecting brings the suggestion back.
        Assert.IsType<NoContentResult>(await sut.UnReject(2, CancellationToken.None));
        var restored = await sut.Get(CancellationToken.None);
        Assert.Contains(restored, s => s.Id == 2);
    }

    [Fact]
    public async Task Reject_Returns_NotFound_For_Unknown_Author()
    {
        await using var db = CreateDb();
        var sut = new RecommendationsController(db);
        Assert.IsType<NotFoundObjectResult>(await sut.Reject(999, CancellationToken.None));
    }

    private static LibraryDbContext CreateDb()
        => new(new DbContextOptionsBuilder<LibraryDbContext>()
            .UseInMemoryDatabase($"recs-tests-{Guid.NewGuid():N}").Options);
}

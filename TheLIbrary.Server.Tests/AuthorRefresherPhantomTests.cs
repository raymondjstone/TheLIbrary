using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class AuthorRefresherPhantomTests
{
    [Fact]
    public async Task RemovePhantomAuthorNameBooks_Deletes_Only_Safe_Author_Named_Books()
    {
        await using var db = NewDb();
        var author = new Author { Id = 1, Name = "Anne McCaffrey" };
        db.Authors.Add(author);
        db.Books.AddRange(
            // Phantom: titled as the author, no files, not owned → DELETE.
            new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W", Title = "Anne McCaffrey", NormalizedTitle = TitleNormalizer.Normalize("Anne McCaffrey") },
            // Real book → keep.
            new Book { Id = 11, AuthorId = 1, OpenLibraryWorkKey = "OL11W", Title = "Partnership", NormalizedTitle = TitleNormalizer.Normalize("Partnership") },
            // Author-named but user marked owned → keep (don't destroy a deliberate entry).
            new Book { Id = 12, AuthorId = 1, OpenLibraryWorkKey = "OL12W", Title = "Anne McCaffrey", NormalizedTitle = TitleNormalizer.Normalize("Anne McCaffrey"), ManuallyOwned = true },
            // Author-named with files mis-matched to it → DELETE, and unmatch the files.
            new Book { Id = 13, AuthorId = 1, OpenLibraryWorkKey = "OL13W", Title = "Anne McCaffrey", NormalizedTitle = TitleNormalizer.Normalize("Anne McCaffrey") });
        db.LocalBookFiles.Add(new LocalBookFile { Id = 50, AuthorId = 1, BookId = 13, FullPath = "/lib/x.epub" });
        await db.SaveChangesAsync();

        var removed = await AuthorRefresher.RemovePhantomAuthorNameBooksAsync(db, author, CancellationToken.None);

        Assert.Equal(2, removed); // 10 and the file-linked 13
        var ids = await db.Books.Select(b => b.Id).OrderBy(i => i).ToListAsync();
        Assert.Equal(new[] { 11, 12 }, ids); // real book + the manually-owned one survive
        Assert.Null((await db.LocalBookFiles.FindAsync(50))!.BookId); // its file is unmatched, not orphaned
    }

    private static LibraryDbContext NewDb()
        => new(new DbContextOptionsBuilder<LibraryDbContext>()
            .UseInMemoryDatabase($"phantom-{System.Guid.NewGuid():N}").Options);
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.IO;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Services.Sync;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

// Covers the ISBN -> work resolution that links author-known/work-less files,
// using a faked OpenLibrary /isbn/{isbn}.json edition endpoint.
public sealed class WorkResolutionTests : IDisposable
{
    private readonly HttpClient _http;

    public WorkResolutionTests()
    {
        _http = new HttpClient(new TestHttpMessageHandler((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            // Edition endpoint: ISBN -> work key + title.
            if (url.Contains("/isbn/9780000000001.json"))
                return Task.FromResult(TestHttpMessageHandler.Json(
                    """{"title":"The Old Gods Awaken","works":[{"key":"/works/OL999W"}],"covers":[42]}"""));
            // A different book entirely — used to prove the title guard rejects a
            // wrong/placeholder ISBN.
            if (url.Contains("/isbn/9780000000002.json"))
                return Task.FromResult(TestHttpMessageHandler.Json(
                    """{"title":"Totally Unrelated Cookbook","works":[{"key":"/works/OL777W"}],"covers":[1]}"""));
            return Task.FromResult(TestHttpMessageHandler.Json("{}", System.Net.HttpStatusCode.NotFound));
        }))
        { BaseAddress = new Uri("https://openlibrary.org/") };
    }

    public void Dispose() => _http.Dispose();

    private OpenLibraryClient NewOl()
    {
        var settings = new OpenLibrarySettings(null!);
        return new OpenLibraryClient(_http, new OpenLibraryRateLimiter(settings), settings, NullLogger<OpenLibraryClient>.Instance);
    }

    private static LibraryDbContext NewDb(string name)
        => new(new DbContextOptionsBuilder<LibraryDbContext>().UseInMemoryDatabase(name).Options);

    [Fact]
    public async Task Links_Work_By_Isbn_Under_Existing_Author()
    {
        var name = "wr-" + Guid.NewGuid().ToString("N");
        await using var db = NewDb(name);
        db.Authors.Add(new Author { Id = 1, Name = "Manley Wade Wellman" });
        var file = new LocalBookFile { Id = 5, AuthorId = 1, AuthorFolder = "Manley Wade Wellman", FullPath = "/lib/MWW/x.epub", ModifiedAt = DateTime.UtcNow };
        db.LocalBookFiles.Add(file);
        await db.SaveChangesAsync();

        var assigner = new UntrackedAuthorAssigner(db, NewOl(), new SystemFileSystem());
        var ok = await assigner.TryLinkWorkByIsbnAsync(file, "978-0-00-000000-1", "The Old Gods Awaken", CancellationToken.None);

        Assert.True(ok);
        Assert.NotNull(file.BookId);
        var book = await db.Books.FindAsync(file.BookId);
        Assert.Equal("OL999W", book!.OpenLibraryWorkKey);
        Assert.Equal(1, book.AuthorId);              // linked under the file's existing author
    }

    [Fact]
    public async Task Rejects_When_Resolved_Title_Grossly_Disagrees()
    {
        var name = "wr2-" + Guid.NewGuid().ToString("N");
        await using var db = NewDb(name);
        db.Authors.Add(new Author { Id = 1, Name = "Manley Wade Wellman" });
        var file = new LocalBookFile { Id = 5, AuthorId = 1, FullPath = "/lib/MWW/x.epub", ModifiedAt = DateTime.UtcNow };
        db.LocalBookFiles.Add(file);
        await db.SaveChangesAsync();

        var assigner = new UntrackedAuthorAssigner(db, NewOl(), new SystemFileSystem());
        // ISBN resolves to "Totally Unrelated Cookbook" but our title is a novel — skip.
        var ok = await assigner.TryLinkWorkByIsbnAsync(file, "9780000000002", "The Old Gods Awaken", CancellationToken.None);

        Assert.False(ok);
        Assert.Null(file.BookId);
    }
}

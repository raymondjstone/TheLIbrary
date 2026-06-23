using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.IO;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Services.Sync;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

public sealed class UnknownAuthorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tl-unknownauth-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = new(new TestHttpMessageHandler((_, _) =>
        Task.FromResult(TestHttpMessageHandler.Json("{}"))));

    public void Dispose() { _http.Dispose(); try { Directory.Delete(_root, true); } catch { } }

    private UntrackedAuthorAssigner NewAssigner(TheLibrary.Server.Data.LibraryDbContext db)
    {
        var settings = new OpenLibrarySettings(null!);
        var ol = new OpenLibraryClient(_http, new OpenLibraryRateLimiter(settings), settings, NullLogger<OpenLibraryClient>.Instance);
        return new UntrackedAuthorAssigner(db, ol, new SystemFileSystem());
    }

    [Fact]
    public async Task AssignToUnknownAuthor_Files_Untracked_File_In_Place_With_No_Book()
    {
        var unknownDir = Path.Combine(_root, CalibreScanner.UnknownAuthorFolder);
        Directory.CreateDirectory(unknownDir);
        var file = Path.Combine(unknownDir, "mystery.epub");
        await File.WriteAllTextAsync(file, "x");

        using var rdb = new RelationalTestDb();
        await using (var s = rdb.NewContext())
        {
            s.LibraryLocations.Add(new LibraryLocation { Id = 1, Label = "L", Path = _root, Enabled = true, IsPrimary = true, CreatedAt = DateTime.UtcNow });
            s.BookContentScans.Add(new BookContentScan { Id = 1, FullPath = file, Source = "untracked", Title = "Mystery", ScannedAt = DateTime.UtcNow });
            await s.SaveChangesAsync();
        }
        await using (var db = rdb.NewContext())
        {
            var assigner = NewAssigner(db);
            var unknown = await assigner.EnsureUnknownAuthorAsync(CancellationToken.None);
            Assert.Equal(UntrackedAuthorAssigner.UnknownAuthorName, unknown.Name);
            Assert.Equal(AuthorStatus.NotFound, unknown.Status);
            Assert.Null(unknown.OpenLibraryKey);
            // Parked far in the future so the refresh-works job never schedules it
            // (it selects authors with NextFetchAt == null / due).
            Assert.True(unknown.NextFetchAt > DateTime.UtcNow.AddYears(100));

            var scan = await db.BookContentScans.FirstAsync();
            var outcome = await assigner.AssignToAuthorAsync(scan, unknown, CancellationToken.None);
            Assert.True(outcome.Assigned, outcome.Reason);
        }
        await using var v = rdb.NewContext();
        var f = await v.LocalBookFiles.FirstAsync();
        var unk = await v.Authors.FirstAsync(a => a.Name == UntrackedAuthorAssigner.UnknownAuthorName);
        Assert.Equal(unk.Id, f.AuthorId);     // filed under Unknown Author
        Assert.Null(f.BookId);                // still unmatched to a book
        Assert.Contains("Unknown Author", f.FullPath.Replace('\\', '/'));
    }

    [Fact]
    public async Task LinkBookKeepingCurrentAuthor_Attaches_Work_Under_Unknown_Author()
    {
        using var rdb = new RelationalTestDb();
        await using (var s = rdb.NewContext())
        {
            s.Authors.Add(new Author { Id = 9, Name = UntrackedAuthorAssigner.UnknownAuthorName, Status = AuthorStatus.NotFound, CreationSource = "manual" });
            s.LocalBookFiles.Add(new LocalBookFile { Id = 1, AuthorId = 9, BookId = null, AuthorFolder = "Unknown Author", FullPath = "/lib/Unknown Author/mystery.epub", ModifiedAt = DateTime.UtcNow });
            s.BookContentScans.Add(new BookContentScan { Id = 1, FullPath = "/lib/Unknown Author/mystery.epub", Source = "unmatched", AuthorId = 9, Title = "Mystery", ScannedAt = DateTime.UtcNow });
            await s.SaveChangesAsync();
        }
        await using (var db = rdb.NewContext())
        {
            var scan = await db.BookContentScans.FirstAsync();
            var outcome = await NewAssigner(db).LinkBookKeepingCurrentAuthorAsync(
                scan, "/works/OL5W", "The Mystery of the Two-Toed Pigeon", 1973, 7, CancellationToken.None);
            Assert.True(outcome.Assigned, outcome.Reason);
        }
        await using var v = rdb.NewContext();
        var book = await v.Books.FirstOrDefaultAsync(b => b.OpenLibraryWorkKey == "OL5W");
        Assert.NotNull(book);
        Assert.Equal(9, book!.AuthorId);                          // book under Unknown Author
        Assert.Equal(book.Id, (await v.LocalBookFiles.FindAsync(1))!.BookId);   // file linked
    }
}

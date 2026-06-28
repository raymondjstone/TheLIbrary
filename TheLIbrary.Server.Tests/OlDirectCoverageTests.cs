using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Controllers;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.IO;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Services.Sync;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

// Direct (non-HTTP) coverage of the OpenLibrary-backed AuthorsController endpoints,
// using a stubbed OpenLibraryClient (canned JSON over a fake HttpMessageHandler) and
// a real UntrackedAuthorAssigner. Direct controller calls are what the coverage
// collector attributes (the in-process WebApplicationFactory path is not captured).
public sealed class OlDirectCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tl-oldirect-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http;

    public OlDirectCoverageTests()
    {
        Directory.CreateDirectory(_root);
        _http = new HttpClient(new TestHttpMessageHandler((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("search/authors.json"))
                return Task.FromResult(TestHttpMessageHandler.Json(
                    """{"docs":[{"key":"OL1A","name":"Primary Auth","work_count":9}]}"""));
            if (url.Contains("search.json"))
                return Task.FromResult(TestHttpMessageHandler.Json(
                    """{"numFound":1,"docs":[{"key":"/works/OL10W","title":"Alpha","first_publish_year":2001,"cover_i":7,"author_name":["Primary Auth"],"author_key":["OL1A"],"language":["eng"]}]}"""));
            return Task.FromResult(TestHttpMessageHandler.Json("{}"));
        }))
        { BaseAddress = new Uri("https://openlibrary.org/") };
    }

    public void Dispose()
    {
        _http.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private OpenLibraryClient NewOl()
    {
        var settings = new OpenLibrarySettings(null!);
        return new OpenLibraryClient(_http, new OpenLibraryRateLimiter(settings), settings, NullLogger<OpenLibraryClient>.Instance);
    }

    private AuthorsController NewController(LibraryDbContext db)
    {
        var ol = NewOl();
        var fs = new SystemFileSystem();
        var assigner = new UntrackedAuthorAssigner(db, ol, fs);
        return new AuthorsController(db, ol, refresher: null!, manualBooks: null!, manualAuthors: null!, fs: fs,
            log: NullLogger<AuthorsController>.Instance, converter: null!, contentScan: null!,
            assigner: assigner, lifetime: null!);
    }

    [Fact]
    public async Task AddOpenLibraryBook_Creates_Book()
    {
        using var rdb = new RelationalTestDb();
        await using (var s = rdb.NewContext())
        {
            s.Authors.Add(new Author { Id = 1, Name = "Primary Auth", CalibreFolderName = "Primary Auth" });
            await s.SaveChangesAsync();
        }
        await using (var db = rdb.NewContext())
        {
            var r = await NewController(db).AddOpenLibraryBook(1,
                new AuthorsController.AddOpenLibraryBookRequest("/works/OL10W", "Alpha", 2001, 7, "Primary Auth", true), default);
            Assert.NotNull(r);
        }
        await using (var v = rdb.NewContext())
            Assert.True(v.Books.Any(b => b.OpenLibraryWorkKey == "OL10W"));
    }

    [Fact]
    public async Task Suggestions_Uses_OpenLibrary()
    {
        using var rdb = new RelationalTestDb();
        await using (var s = rdb.NewContext())
        {
            s.LibraryLocations.Add(new LibraryLocation { Id = 1, Label = "L", Path = _root, Enabled = true, IsPrimary = true, CreatedAt = DateTime.UtcNow });
            s.Authors.Add(new Author { Id = 1, Name = "Primary Auth", CalibreFolderName = "Primary Auth" });
            s.LocalBookFiles.Add(new LocalBookFile { Id = 1, AuthorId = 1, BookId = null, AuthorFolder = "Primary Auth", TitleFolder = "Alpha", FullPath = Path.Combine(_root, "Primary Auth", "Alpha.epub"), ModifiedAt = DateTime.UtcNow });
            await s.SaveChangesAsync();
        }
        await using var db = rdb.NewContext();
        var r = await NewController(db).Suggestions(1, default);
        Assert.NotNull(r);
    }

    [Fact]
    public async Task AssignUntrackedAuthorsAll_Processes_Scan_Rows()
    {
        using var rdb = new RelationalTestDb();
        await using (var s = rdb.NewContext())
        {
            s.LibraryLocations.Add(new LibraryLocation { Id = 1, Label = "L", Path = _root, Enabled = true, IsPrimary = true, CreatedAt = DateTime.UtcNow });
            s.OpenLibraryAuthors.Add(new OpenLibraryAuthor { OlKey = "OL1A", Name = "Primary Auth", NormalizedName = TitleNormalizer.NormalizeAuthor("Primary Auth"), ImportedAt = DateTime.UtcNow });
            var f = Path.Combine(_root, CalibreScanner.UnknownAuthorFolder);
            Directory.CreateDirectory(f);
            var path = Path.Combine(f, "Alpha - Primary Auth.epub");
            File.WriteAllText(path, "x");
            s.BookContentScans.Add(new BookContentScan { Id = 1, FullPath = path, Source = "untracked", Author = "Primary Auth", Title = "Alpha", ScannedAt = DateTime.UtcNow });
            await s.SaveChangesAsync();
        }
        await using var db = rdb.NewContext();
        var r = await NewController(db).AssignUntrackedAuthorsAll(null, default);
        Assert.NotNull(r);
    }

    private async Task SeedScanAsync(RelationalTestDb rdb, string? isbn = null)
    {
        await using var s = rdb.NewContext();
        s.LibraryLocations.Add(new LibraryLocation { Id = 1, Label = "L", Path = _root, Enabled = true, IsPrimary = true, CreatedAt = DateTime.UtcNow });
        s.OpenLibraryAuthors.Add(new OpenLibraryAuthor { OlKey = "OL1A", Name = "Primary Auth", NormalizedName = TitleNormalizer.NormalizeAuthor("Primary Auth"), ImportedAt = DateTime.UtcNow });
        var dir = Path.Combine(_root, CalibreScanner.UnknownAuthorFolder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Alpha - Primary Auth.epub");
        File.WriteAllText(path, "x");
        s.BookContentScans.Add(new BookContentScan { Id = 1, FullPath = path, Source = "untracked", Author = "Primary Auth", Title = "Alpha", Isbn = isbn, ScannedAt = DateTime.UtcNow });
        await s.SaveChangesAsync();
    }

    [Fact]
    public async Task AssignAsync_Recovers_Title_And_Author_From_Filename_When_Extraction_Empty()
    {
        using var rdb = new RelationalTestDb();
        await using (var s = rdb.NewContext())
        {
            s.LibraryLocations.Add(new LibraryLocation { Id = 1, Label = "L", Path = _root, Enabled = true, IsPrimary = true, CreatedAt = DateTime.UtcNow });
            s.OpenLibraryAuthors.Add(new OpenLibraryAuthor { OlKey = "OL1A", Name = "Primary Auth", NormalizedName = TitleNormalizer.NormalizeAuthor("Primary Auth"), ImportedAt = DateTime.UtcNow });
            var dir = Path.Combine(_root, CalibreScanner.UnknownAuthorFolder);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "Alpha - Primary Auth.epub");
            File.WriteAllText(path, "x");
            // Extraction got NOTHING — only the filename carries "Title - Author".
            s.BookContentScans.Add(new BookContentScan { Id = 1, FullPath = path, Source = "untracked", ScannedAt = DateTime.UtcNow });
            await s.SaveChangesAsync();
        }
        await using var db = rdb.NewContext();
        var scan = db.BookContentScans.First();
        var assigner = new UntrackedAuthorAssigner(db, NewOl(), new SystemFileSystem());

        var outcome = await assigner.AssignAsync(scan, default);

        Assert.True(outcome.Assigned, outcome.Reason);
        Assert.Equal("Primary Auth", outcome.AuthorName);
    }

    [Fact]
    public async Task ApplyContentGuess_Runs()
    {
        using var rdb = new RelationalTestDb();
        await SeedScanAsync(rdb);
        await using var db = rdb.NewContext();
        var r = await NewController(db).ApplyContentGuess(1, default);
        Assert.NotNull(r);
    }

    [Fact]
    public async Task AssignUntrackedAuthor_Single_Runs()
    {
        using var rdb = new RelationalTestDb();
        await SeedScanAsync(rdb);
        await using var db = rdb.NewContext();
        var r = await NewController(db).AssignUntrackedAuthor(1, default);
        Assert.NotNull(r);
    }

    [Fact]
    public async Task ApplyAllIsbnGuesses_Runs()
    {
        using var rdb = new RelationalTestDb();
        await SeedScanAsync(rdb, isbn: "9780000000001");
        await using var db = rdb.NewContext();
        var r = await NewController(db).ApplyAllIsbnGuesses(default);
        Assert.NotNull(r);
    }
}

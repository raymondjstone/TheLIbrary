using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TheLibrary.Server.Services.Sync;
using TheLibrary.Server.Controllers;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

[Collection("Integration")]
public class AuthorsControllerIntegrationTests
{
    [Fact]
    public async Task SetPriority_Rejects_Out_Of_Range_Value()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db => db.Authors.Add(new Author { Id = 1, Name = "Author" }));
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/authors/1/priority", new AuthorsController.SetPriorityRequest(6));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SetPriority_Updates_Author()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db => db.Authors.Add(new Author { Id = 1, Name = "Author", Priority = 0 }));
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/authors/1/priority", new AuthorsController.SetPriorityRequest(4));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        Assert.Equal(4, (await db.Authors.FindAsync(1))!.Priority);
    }

    [Fact]
    public async Task SetRefreshInterval_Rejects_Invalid_Range()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db => db.Authors.Add(new Author { Id = 1, Name = "Author" }));
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/authors/1/refresh-interval", new AuthorsController.SetRefreshIntervalRequest(0));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SaveNotes_Trims_Text_And_Persists()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db => db.Authors.Add(new Author { Id = 1, Name = "Author" }));
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/authors/1/notes", new AuthorsController.SaveNotesRequest("  some notes  "));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        Assert.Equal("some notes", (await db.Authors.FindAsync(1))!.Notes);
    }

    [Fact]
    public async Task AddBook_Creates_Manual_Book_For_Author()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db => db.Authors.Add(new Author { Id = 1, Name = "Author" }));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/authors/1/books", new AuthorsController.AddBookRequest("Manual Title", 2005, "Saga", "1", true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var book = Assert.Single(db.Books.Where(b => b.AuthorId == 1));
        Assert.StartsWith("XX", book.OpenLibraryWorkKey, StringComparison.Ordinal);
        Assert.Equal("Manual Title", book.Title);
        Assert.True(book.ManuallyOwned);
    }

    [Fact]
    public async Task AddOpenLibraryBook_Creates_Work_For_Author()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db => db.Authors.Add(new Author { Id = 1, Name = "Author" }));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/authors/1/books/openlibrary",
            new AuthorsController.AddOpenLibraryBookRequest("/works/OL123W", "Open Work", 1986, 42, "Author", true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var book = Assert.Single(db.Books.Where(b => b.AuthorId == 1));
        Assert.Equal("OL123W", book.OpenLibraryWorkKey);
        Assert.Equal("Open Work", book.Title);
        Assert.True(book.ManuallyOwned);
    }

    [Fact]
    public async Task MatchLocalFileToOpenLibraryWork_Creates_Book_And_Matches_File()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "Author", OpenLibraryKey = "OL1A" });
            db.LocalBookFiles.Add(new LocalBookFile
            {
                Id = 10,
                AuthorId = 1,
                AuthorFolder = "Author",
                TitleFolder = "Open Work",
                FullPath = "C:\\Lib\\Author\\Open Work",
                ManuallyUnmatched = true,
            });
            db.LibraryLocations.Add(new LibraryLocation
            {
                Id = 1,
                Label = "Default",
                Path = "C:\\Lib",
                Enabled = true,
                CreatedAt = DateTime.UtcNow,
            });
        });
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/authors/1/unmatched/10/openlibrary-match",
            new AuthorsController.MatchOpenLibraryFileRequest("/works/OL123W", "Open Work", 1986, 42, "Author", "OL1A", "Author"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var book = Assert.Single(db.Books);
        var file = Assert.Single(db.LocalBookFiles);
        Assert.Equal(book.Id, file.BookId);
        Assert.False(file.ManuallyUnmatched);
    }

    [Fact]
    public async Task GetUntrackedContents_Lists_Nested_Files_For_Unclaimed_Folder()
    {
        using var factory = new LibraryApiFactory();
        var root = Path.Combine(Path.GetTempPath(), $"thelibrary-untracked-{Guid.NewGuid():N}");
        var authorDir = Path.Combine(root, "Loose Author");
        var nestedDir = Path.Combine(authorDir, "Series Shelf");
        Directory.CreateDirectory(nestedDir);
        await File.WriteAllTextAsync(Path.Combine(nestedDir, "Loose Match.epub"), "test");

        try
        {
            await SeedAsync(factory, db =>
            {
                db.LibraryLocations.Add(new LibraryLocation { Id = 1, Label = "Default", Path = root, Enabled = true, CreatedAt = DateTime.UtcNow });
                db.LocalBookFiles.Add(new LocalBookFile { Id = 1, AuthorFolder = "Loose Author", TitleFolder = "Series Shelf", FullPath = Path.Combine(authorDir, "Series Shelf") });
            });
            using var client = factory.CreateClient();

            var res = await client.GetFromJsonAsync<AuthorsController.UntrackedFolderContents>($"/api/untracked/contents?bucket=unclaimed&folder={Uri.EscapeDataString("Loose Author")}&rootPath={Uri.EscapeDataString(root)}&path={Uri.EscapeDataString("Series Shelf")}");

            Assert.NotNull(res);
            Assert.Equal("Series Shelf", res!.CurrentPath);
            Assert.Contains(res.Entries, e => e.RelativePath == "Series Shelf/Loose Match.epub" && !e.IsDirectory);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MatchUntrackedToOpenLibrary_Moves_File_And_Creates_Book()
    {
        using var factory = new LibraryApiFactory();
        var root = Path.Combine(Path.GetTempPath(), $"thelibrary-untracked-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(root, CalibreScanner.UnknownAuthorFolder, "Loose Author", "Series Shelf");
        Directory.CreateDirectory(sourceDir);
        var sourceFile = Path.Combine(sourceDir, "Loose Match.epub");
        await File.WriteAllTextAsync(sourceFile, "test");

        try
        {
            await SeedAsync(factory, db =>
            {
                db.LibraryLocations.Add(new LibraryLocation { Id = 1, Label = "Default", Path = root, Enabled = true, CreatedAt = DateTime.UtcNow });
            });
            using var client = factory.CreateClient();

            var response = await client.PostAsJsonAsync("/api/untracked/match-openlibrary",
                new AuthorsController.MatchUntrackedOpenLibraryRequest(
                    "unknown",
                    "Loose Author",
                    root,
                    "Series Shelf/Loose Match.epub",
                    "/works/OL999W",
                    "Loose Match",
                    2001,
                    42,
                    "Target Author",
                    "OL123A",
                    "Target Author"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            var author = Assert.Single(db.Authors);
            var book = Assert.Single(db.Books);
            var file = Assert.Single(db.LocalBookFiles);
            Assert.Equal(author.Id, file.AuthorId);
            Assert.Equal(book.Id, file.BookId);
            Assert.DoesNotContain($"{Path.DirectorySeparatorChar}__unknown{Path.DirectorySeparatorChar}", file.FullPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(file.FullPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AssignUntrackedAuthor_Resolves_Via_OpenLibrary_And_Files_Under_Author()
    {
        using var factory = new LibraryApiFactory((request, _) =>
            Task.FromResult(TestHttpMessageHandler.Json("""
                {"numFound":1,"docs":[{"key":"/works/OL55W","title":"Found Book",
                  "author_name":["Target Author"],"author_key":["OL123A"]}]}
                """)));
        var root = Path.Combine(Path.GetTempPath(), $"thelibrary-untracked-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(root, CalibreScanner.UnknownAuthorFolder, "Loose");
        Directory.CreateDirectory(sourceDir);
        var sourceFile = Path.Combine(sourceDir, "mystery.epub");
        await File.WriteAllTextAsync(sourceFile, "x");
        try
        {
            await SeedAsync(factory, db =>
            {
                db.LibraryLocations.Add(new LibraryLocation { Id = 1, Label = "Default", Path = root, Enabled = true, CreatedAt = DateTime.UtcNow });
                db.BookContentScans.Add(new BookContentScan
                {
                    Id = 9, FullPath = sourceFile, Source = "untracked",
                    Isbn = "9780000000001", Title = "Found Book", Author = "Target Author", ScannedAt = DateTime.UtcNow,
                });
            });
            using var client = factory.CreateClient();

            var resp = await client.PostAsync("/api/identified/9/assign-author", null);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            var file = Assert.Single(db.LocalBookFiles);
            Assert.NotNull(file.AuthorId);                  // filed under the resolved author
            Assert.NotNull(file.BookId);                    // and linked to the OL book
            Assert.DoesNotContain("__unknown", file.FullPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(file.FullPath));         // moved on disk
            Assert.True((await db.BookContentScans.FindAsync(9))!.Reviewed);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AssignUntrackedAuthor_Falls_Back_To_Existing_Author_By_Name()
    {
        using var factory = new LibraryApiFactory();
        var root = Path.Combine(Path.GetTempPath(), $"thelibrary-untracked-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(root, CalibreScanner.UnknownAuthorFolder, "Loose");
        Directory.CreateDirectory(sourceDir);
        var sourceFile = Path.Combine(sourceDir, "loose.epub");
        await File.WriteAllTextAsync(sourceFile, "x");
        try
        {
            await SeedAsync(factory, db =>
            {
                db.LibraryLocations.Add(new LibraryLocation { Id = 1, Label = "Default", Path = root, Enabled = true, CreatedAt = DateTime.UtcNow });
                db.Authors.Add(new Author { Id = 7, Name = "Known Author" });
                // No ISBN/title → OpenLibrary isn't consulted; the guessed name is.
                db.BookContentScans.Add(new BookContentScan
                {
                    Id = 9, FullPath = sourceFile, Source = "untracked", Author = "Known Author", ScannedAt = DateTime.UtcNow,
                });
            });
            using var client = factory.CreateClient();

            var resp = await client.PostAsync("/api/identified/9/assign-author", null);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            Assert.Single(await db.Authors.ToListAsync());   // reused existing, none created
            var file = Assert.Single(db.LocalBookFiles);
            Assert.Equal(7, file.AuthorId);
            Assert.Null(file.BookId);                        // author-only — no book yet
            Assert.DoesNotContain("__unknown", file.FullPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AssignUntrackedAuthorsAll_Files_Every_Untracked_Row_And_Keeps_Catalogue_Rows()
    {
        using var factory = new LibraryApiFactory();
        var root = Path.Combine(Path.GetTempPath(), $"thelibrary-untracked-{Guid.NewGuid():N}");
        var dir = Path.Combine(root, CalibreScanner.UnknownAuthorFolder, "Loose");
        Directory.CreateDirectory(dir);
        var f1 = Path.Combine(dir, "a.epub");
        var f2 = Path.Combine(dir, "b.epub");
        await File.WriteAllTextAsync(f1, "x");
        await File.WriteAllTextAsync(f2, "x");
        try
        {
            await SeedAsync(factory, db =>
            {
                db.LibraryLocations.Add(new LibraryLocation { Id = 1, Label = "D", Path = root, Enabled = true, CreatedAt = DateTime.UtcNow });
                db.OpenLibraryAuthors.AddRange(
                    new OpenLibraryAuthor { OlKey = "OL1A", Name = "Author One", NormalizedName = TheLibrary.Server.Services.Sync.TitleNormalizer.NormalizeAuthor("Author One"), ImportedAt = DateTime.UtcNow },
                    new OpenLibraryAuthor { OlKey = "OL2A", Name = "Author Two", NormalizedName = TheLibrary.Server.Services.Sync.TitleNormalizer.NormalizeAuthor("Author Two"), ImportedAt = DateTime.UtcNow });
                db.BookContentScans.AddRange(
                    new BookContentScan { Id = 1, FullPath = f1, Source = "untracked", Author = "Author One", SeriesCatalogJson = "[{\"Series\":\"S\",\"Titles\":[\"T\"]}]", ScannedAt = DateTime.UtcNow },
                    new BookContentScan { Id = 2, FullPath = f2, Source = "untracked", Author = "Author Two", ScannedAt = DateTime.UtcNow });
            });
            using var client = factory.CreateClient();

            var resp = await client.PostAsync("/api/identified/assign-authors-all", null);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadFromJsonAsync<AuthorsController.AssignAuthorsAllResult>();
            Assert.Equal(2, body!.Assigned);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            Assert.Equal(2, await db.Authors.CountAsync());
            Assert.Equal(2, await db.LocalBookFiles.CountAsync(f => f.AuthorId != null));
            // The catalogue row is kept (tagged with its author) so series can still be built…
            var withCat = await db.BookContentScans.FindAsync(1);
            Assert.False(withCat!.Reviewed);
            Assert.NotNull(withCat.AuthorId);
            // …while the catalogue-less row is cleared from the review list.
            Assert.True((await db.BookContentScans.FindAsync(2))!.Reviewed);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AssignUntrackedAuthor_Creates_Pending_Author_Only_When_Name_Is_A_Known_OL_Author()
    {
        using var factory = new LibraryApiFactory();
        var root = Path.Combine(Path.GetTempPath(), $"thelibrary-untracked-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(root, CalibreScanner.UnknownAuthorFolder, "Loose");
        Directory.CreateDirectory(sourceDir);
        var sourceFile = Path.Combine(sourceDir, "loose.epub");
        await File.WriteAllTextAsync(sourceFile, "x");
        try
        {
            await SeedAsync(factory, db =>
            {
                db.LibraryLocations.Add(new LibraryLocation { Id = 1, Label = "Default", Path = root, Enabled = true, CreatedAt = DateTime.UtcNow });
                db.OpenLibraryAuthors.Add(new OpenLibraryAuthor
                {
                    OlKey = "OL777A", Name = "Catalogued Author",
                    NormalizedName = TheLibrary.Server.Services.Sync.TitleNormalizer.NormalizeAuthor("Catalogued Author"), ImportedAt = DateTime.UtcNow,
                });
                db.BookContentScans.Add(new BookContentScan
                {
                    Id = 9, FullPath = sourceFile, Source = "untracked", Author = "Catalogued Author", ScannedAt = DateTime.UtcNow,
                });
            });
            using var client = factory.CreateClient();

            var resp = await client.PostAsync("/api/identified/9/assign-author", null);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            var author = Assert.Single(db.Authors);          // created from the OL catalogue row
            Assert.Equal("OL777A", author.OpenLibraryKey);
            Assert.Equal(AuthorStatus.Pending, author.Status);
            Assert.Equal(author.Id, Assert.Single(db.LocalBookFiles).AuthorId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AssignUntrackedAuthor_Refuses_When_Name_Is_Not_A_Known_OL_Author()
    {
        using var factory = new LibraryApiFactory();
        var root = Path.Combine(Path.GetTempPath(), $"thelibrary-untracked-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(root, CalibreScanner.UnknownAuthorFolder, "Loose");
        Directory.CreateDirectory(sourceDir);
        var sourceFile = Path.Combine(sourceDir, "loose.epub");
        await File.WriteAllTextAsync(sourceFile, "x");
        try
        {
            await SeedAsync(factory, db =>
            {
                db.LibraryLocations.Add(new LibraryLocation { Id = 1, Label = "Default", Path = root, Enabled = true, CreatedAt = DateTime.UtcNow });
                // No matching Author and no OpenLibraryAuthors row for this name.
                db.BookContentScans.Add(new BookContentScan
                {
                    Id = 9, FullPath = sourceFile, Source = "untracked", Author = "Nobody In Particular", ScannedAt = DateTime.UtcNow,
                });
            });
            using var client = factory.CreateClient();

            var resp = await client.PostAsync("/api/identified/9/assign-author", null);
            var body = await resp.Content.ReadFromJsonAsync<AuthorsController.AssignAuthorResult>();

            Assert.False(body!.Assigned);
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            Assert.Empty(await db.Authors.ToListAsync());     // nothing invented
            Assert.Empty(await db.LocalBookFiles.ToListAsync());
            Assert.True(File.Exists(sourceFile));             // file left where it was
            Assert.False((await db.BookContentScans.FindAsync(9))!.Reviewed);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AddAuthor_Rejects_Blacklisted_Name_When_Name_Is_Provided()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db =>
        {
            db.AuthorBlacklist.Add(new AuthorBlacklist
            {
                Name = "Blocked Author",
                NormalizedName = TheLibrary.Server.Services.Sync.TitleNormalizer.NormalizeAuthor("Blocked Author")
            });
        });
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/authors", new AuthorsController.AddAuthorRequest
        {
            OpenLibraryKey = "OL1A",
            Name = "Blocked Author"
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Starred_Returns_Aggregated_Counts()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "Starred", Priority = 3 });
            db.Books.Add(new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W", Title = "Owned Ebook", NormalizedTitle = "owned ebook" });
            db.Books.Add(new Book { Id = 11, AuthorId = 1, OpenLibraryWorkKey = "OL11W", Title = "Physical", NormalizedTitle = "physical", ManuallyOwned = true });
            db.LocalBookFiles.Add(new LocalBookFile { Id = 50, AuthorId = 1, BookId = 10, AuthorFolder = "Starred", TitleFolder = "Owned Ebook", FullPath = "C:\\Lib\\Starred\\Owned Ebook\\book.epub" });
            db.LocalBookFiles.Add(new LocalBookFile { Id = 51, AuthorId = 1, BookId = null, AuthorFolder = "Starred", TitleFolder = "Unmatched", FullPath = "C:\\Lib\\Starred\\Unmatched\\book.epub" });
        });
        using var client = factory.CreateClient();

        var rows = await client.GetFromJsonAsync<List<AuthorsController.StarredAuthorRow>>("/api/authors/starred");

        var row = Assert.Single(rows!);
        Assert.Equal(2, row.BookCount);
        Assert.Equal(1, row.EbookCount);
        Assert.Equal(1, row.UnmatchedCount);
    }

    [Fact]
    public async Task Starred_UnmatchedCount_Excludes_NonEbook_Blank_And_Archived_Rows()
    {
        // Matches what the author detail page actually shows: only real ebook files
        // with a non-blank title that aren't archived. The folder-shaped, blank-title
        // and archived rows here must NOT inflate the star count.
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "Starred", Priority = 3 });
            db.LocalBookFiles.AddRange(
                new LocalBookFile { Id = 50, AuthorId = 1, BookId = null, AuthorFolder = "Starred", TitleFolder = "Real", FullPath = "/lib/Starred/Real/book.epub" },
                new LocalBookFile { Id = 51, AuthorId = 1, BookId = null, AuthorFolder = "Starred", TitleFolder = "", FullPath = "/lib/Starred" },
                new LocalBookFile { Id = 52, AuthorId = 1, BookId = null, AuthorFolder = "Starred", TitleFolder = "FolderRow", FullPath = "/lib/Starred/FolderRow" },
                new LocalBookFile { Id = 53, AuthorId = 1, BookId = null, AuthorFolder = "Starred", TitleFolder = "Arch", FullPath = "/lib/__archive/Starred/old.epub" });
        });
        using var client = factory.CreateClient();

        var rows = await client.GetFromJsonAsync<List<AuthorsController.StarredAuthorRow>>("/api/authors/starred");

        Assert.Equal(1, Assert.Single(rows!).UnmatchedCount); // only the real .epub
    }

    [Fact]
    public async Task Link_Rejects_Self_Link()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db => db.Authors.Add(new Author { Id = 1, Name = "Author" }));
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/authors/1/link", new AuthorsController.LinkAuthorRequest(1, false));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AuthorDetail_Flags_Archived_Files()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "Author" });
            db.Books.Add(new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL1W", Title = "Book", NormalizedTitle = "book" });
            db.LocalBookFiles.AddRange(
                new LocalBookFile { Id = 1, BookId = 10, AuthorId = 1, FullPath = "/lib/Author/book.epub" },
                new LocalBookFile { Id = 2, BookId = 10, AuthorId = 1, FullPath = "/lib/__archive/Author/book.pdf" });
        });

        using var client = factory.CreateClient();
        var detail = await client.GetFromJsonAsync<AuthorsController.AuthorDetail>("/api/authors/1");

        var book = Assert.Single(detail!.Books);
        Assert.False(book.Files.Single(f => f.Id == 1).Archived); // live copy
        Assert.True(book.Files.Single(f => f.Id == 2).Archived);  // under __archive
    }

    [Fact]
    public async Task ApplyContentGuess_Links_File_To_OpenLibrary_Work_By_Isbn()
    {
        // OpenLibrary ISBN search returns one work.
        using var factory = new LibraryApiFactory((request, _) =>
        {
            Assert.Contains("search.json", request.RequestUri!.ToString());
            return Task.FromResult(TestHttpMessageHandler.Json("""
                {"numFound":1,"docs":[{"key":"/works/OL77W","title":"Dragonflight",
                  "first_publish_year":1968,"author_name":["Anne McCaffrey"],"author_key":["OL26320A"]}]}
                """));
        });
        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "Anne McCaffrey", OpenLibraryKey = "OL26320A" });
            db.LocalBookFiles.Add(new LocalBookFile { Id = 5, AuthorId = 1, FullPath = "/lib/A/df.epub" });
            db.BookContentScans.Add(new BookContentScan
            {
                Id = 9, FullPath = "/lib/A/df.epub", Source = "unmatched", AuthorId = 1,
                Isbn = "9780345334916", Title = "Dragonflight", Author = "Anne McCaffrey",
            });
        });

        using var client = factory.CreateClient();
        var resp = await client.PostAsync("/api/authors/apply-content-guess/9", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var file = await db.LocalBookFiles.FindAsync(5);
        Assert.NotNull(file!.BookId);                         // now matched to a book
        var book = await db.Books.FindAsync(file.BookId);
        Assert.Equal("OL77W", book!.OpenLibraryWorkKey);
        Assert.True((await db.BookContentScans.FindAsync(9))!.Reviewed); // cleared from review list
    }

    [Fact]
    public async Task ApplyAllIsbn_Applies_Every_Isbn_Backed_Guess()
    {
        using var factory = new LibraryApiFactory((request, _) =>
            Task.FromResult(TestHttpMessageHandler.Json("""
                {"numFound":1,"docs":[{"key":"/works/OLXW","title":"A Work","author_name":["Anne McCaffrey"],"author_key":["OL26320A"]}]}
                """)));
        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "Anne McCaffrey", OpenLibraryKey = "OL26320A" });
            db.LocalBookFiles.AddRange(
                new LocalBookFile { Id = 1, AuthorId = 1, FullPath = "/lib/A/a.epub" },
                new LocalBookFile { Id = 2, AuthorId = 1, FullPath = "/lib/A/b.epub" });
            db.BookContentScans.AddRange(
                new BookContentScan { Id = 1, FullPath = "/lib/A/a.epub", Source = "unmatched", AuthorId = 1, Isbn = "9780345334916" },
                new BookContentScan { Id = 2, FullPath = "/lib/A/b.epub", Source = "unmatched", AuthorId = 1, Isbn = "9780345335463" },
                // No ISBN → not part of the bulk-ISBN apply.
                new BookContentScan { Id = 3, FullPath = "/lib/A/c.epub", Source = "unmatched", AuthorId = 1, Title = "Title only" });
        });

        using var client = factory.CreateClient();
        var resp = await client.PostAsync("/api/identified/apply-isbn-all", null);
        var result = await resp.Content.ReadFromJsonAsync<AuthorsController.BulkApplyResult>();

        Assert.Equal(2, result!.Applied);
        Assert.Equal(0, result.Remaining);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        Assert.NotNull((await db.LocalBookFiles.FindAsync(1))!.BookId);
        Assert.NotNull((await db.LocalBookFiles.FindAsync(2))!.BookId);
        Assert.False((await db.BookContentScans.FindAsync(3))!.Reviewed); // title-only guess untouched
    }

    [Fact]
    public async Task ApplyContentGuess_By_Isbn_Keeps_Folder_Author_Even_When_Work_Has_Another_Author()
    {
        // An ISBN is definitive, so the work is applied — but the OL work lists a
        // *different* author. Because the file already sits in an author folder,
        // that folder is the author: the file stays put and no foreign author is
        // created. (The title path, by contrast, refuses a different-author work
        // — see ApplyContentGuess_By_Title_Refuses_Work_By_Another_Author.)
        using var factory = new LibraryApiFactory((request, _) =>
        {
            Assert.Contains("search.json", request.RequestUri!.ToString());
            return Task.FromResult(TestHttpMessageHandler.Json("""
                {"numFound":1,"docs":[{"key":"/works/OL99W","title":"Some Book",
                  "author_name":["Someone Else"],"author_key":["OL999A"]}]}
                """));
        });
        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "Anne McCaffrey", OpenLibraryKey = "OL26320A" });
            db.LocalBookFiles.Add(new LocalBookFile { Id = 5, AuthorId = 1, FullPath = "/lib/A/x.epub" });
            db.BookContentScans.Add(new BookContentScan
            {
                Id = 9, FullPath = "/lib/A/x.epub", Source = "unmatched", AuthorId = 1,
                Isbn = "9780000000001", Title = "Some Book", Author = "Someone Else",
            });
        });

        using var client = factory.CreateClient();
        var resp = await client.PostAsync("/api/authors/apply-content-guess/9", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var file = await db.LocalBookFiles.FindAsync(5);
        Assert.Equal(1, file!.AuthorId);                       // unchanged — folder is the author
        var book = await db.Books.FindAsync(file.BookId);
        Assert.Equal(1, book!.AuthorId);                       // work attached under the folder author
        Assert.Single(await db.Authors.ToListAsync());         // no "Someone Else" author created
    }

    [Fact]
    public async Task ApplyContentGuess_By_Title_Refuses_When_No_Known_Title_Matches()
    {
        // Title-only guess that matches none of the author's own books. It must be
        // refused, not invented from OpenLibrary: the file stays unmatched and no
        // book is created. (No OL call is made on the title path at all.)
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "Anne McCaffrey" });
            db.Books.Add(new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W",
                Title = "Dragonflight", NormalizedTitle = TitleNormalizer.Normalize("Dragonflight") });
            db.LocalBookFiles.Add(new LocalBookFile { Id = 5, AuthorId = 1, FullPath = "/lib/A/x.epub" });
            db.BookContentScans.Add(new BookContentScan
            {
                Id = 9, FullPath = "/lib/A/x.epub", Source = "unmatched", AuthorId = 1,
                Title = "Some Completely Unrelated Book",
            });
        });

        using var client = factory.CreateClient();
        var resp = await client.PostAsync("/api/authors/apply-content-guess/9", null);
        var body = await resp.Content.ReadFromJsonAsync<AuthorsController.ApplyGuessResult>();

        Assert.False(body!.Applied);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        Assert.Null((await db.LocalBookFiles.FindAsync(5))!.BookId); // still unmatched
        Assert.Single(await db.Books.ToListAsync());                 // no new book invented
    }

    [Fact]
    public async Task ApplyContentGuess_By_Title_Links_To_The_Authors_Own_Known_Book()
    {
        // Title-only guess. No OpenLibrary call — it matches the guess against the
        // author's OWN existing books (the DB list of valid titles) and links the
        // file to the one that matches, even with a trailing "Book N" descriptor.
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "Terry Brooks" });
            db.Books.Add(new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W",
                Title = "High Druid of Shannara: Jarka Ruus",
                NormalizedTitle = TitleNormalizer.Normalize("High Druid of Shannara: Jarka Ruus") });
            db.LocalBookFiles.Add(new LocalBookFile { Id = 5, AuthorId = 1, FullPath = "/lib/A/x.epub" });
            db.BookContentScans.Add(new BookContentScan
            {
                Id = 9, FullPath = "/lib/A/x.epub", Source = "unmatched", AuthorId = 1,
                Title = "High Druid of Shannara - Book 1",
            });
        });

        using var client = factory.CreateClient();
        var resp = await client.PostAsync("/api/authors/apply-content-guess/9", null);
        var body = await resp.Content.ReadFromJsonAsync<AuthorsController.ApplyGuessResult>();

        Assert.True(body!.Applied);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var file = await db.LocalBookFiles.FindAsync(5);
        Assert.Equal(10, file!.BookId);              // linked to the author's existing book
        Assert.Single(await db.Books.ToListAsync()); // no new book invented
    }

    [Fact]
    public async Task Suggestions_Never_Offers_A_Book_Titled_As_The_Author()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "Anne McCaffrey" });
            db.Books.AddRange(
                new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W", Title = "Partnership", NormalizedTitle = TitleNormalizer.Normalize("Partnership") },
                // The OL artifact: a "book" titled as the author.
                new Book { Id = 11, AuthorId = 1, OpenLibraryWorkKey = "OL11W", Title = "Anne McCaffrey", NormalizedTitle = TitleNormalizer.Normalize("Anne McCaffrey") });
            db.LocalBookFiles.Add(new LocalBookFile
            {
                Id = 50, AuthorId = 1, BookId = null, AuthorFolder = "Anne McCaffrey_OL19880A",
                TitleFolder = "Partner Ship - Anne McCaffrey",
                NormalizedTitle = TitleNormalizer.Normalize("Partner Ship"),
                FullPath = "/Books/Collection/Anne McCaffrey_OL19880A/Partner Ship - Anne McCaffrey.pdf",
            });
        });
        using var client = factory.CreateClient();

        var sets = await client.GetFromJsonAsync<List<AuthorsController.FileSuggestionSet>>("/api/authors/1/unmatched/suggestions?top=5");

        var set = Assert.Single(sets!);
        Assert.DoesNotContain(set.Candidates, c => c.Title == "Anne McCaffrey"); // author-name book never suggested
        Assert.Contains(set.Candidates, c => c.Title == "Partnership");
    }

    [Fact]
    public async Task CleanupNameBooks_Sweeps_Whole_Library_Keeping_Safe_Rows()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db =>
        {
            db.Authors.AddRange(
                new Author { Id = 1, Name = "Anne McCaffrey" },
                new Author { Id = 2, Name = "Dave Duncan" });
            db.Books.AddRange(
                // Two phantoms (different authors) with nothing linked → deleted.
                new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W", Title = "Anne McCaffrey", NormalizedTitle = TitleNormalizer.Normalize("Anne McCaffrey") },
                new Book { Id = 20, AuthorId = 2, OpenLibraryWorkKey = "OL20W", Title = "Dave Duncan", NormalizedTitle = TitleNormalizer.Normalize("Dave Duncan") },
                // A real book → kept.
                new Book { Id = 11, AuthorId = 1, OpenLibraryWorkKey = "OL11W", Title = "Partnership", NormalizedTitle = TitleNormalizer.Normalize("Partnership") },
                // Phantom WITH files mis-matched (like Alan Dean Foster) → also deleted, files unmatched.
                new Book { Id = 21, AuthorId = 2, OpenLibraryWorkKey = "OL21W", Title = "Dave Duncan", NormalizedTitle = TitleNormalizer.Normalize("Dave Duncan") });
            db.LocalBookFiles.Add(new LocalBookFile { Id = 50, AuthorId = 2, BookId = 21, FullPath = "/lib/d.epub" });
        });
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/authors/cleanup-name-books", null);
        var body = await resp.Content.ReadFromJsonAsync<AuthorsController.CleanupNameBooksResult>();

        Assert.Equal(3, body!.Removed); // 10, 20, and the file-linked 21
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var ids = await db.Books.Select(b => b.Id).OrderBy(i => i).ToListAsync();
        Assert.Equal(new[] { 11 }, ids);                 // only the real book remains
        Assert.Null((await db.LocalBookFiles.FindAsync(50))!.BookId); // file unmatched, not orphaned
    }

    [Fact]
    public async Task Suggestions_Do_Not_Match_Files_To_An_Author_Prefixed_Junk_Book()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "Alan Dean Foster" });
            db.Books.AddRange(
                // Junk book whose title starts with the author name (filename-derived).
                new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W", Title = "Alan Dean Foster - Alien - Ala", NormalizedTitle = TitleNormalizer.Normalize("Alan Dean Foster - Alien - Ala") },
                // The real book for one of the files.
                new Book { Id = 11, AuthorId = 1, OpenLibraryWorkKey = "OL11W", Title = "Codgerspace", NormalizedTitle = TitleNormalizer.Normalize("Codgerspace") });
            // File whose real title (Oshenerth) has NO owning book → must get no junk suggestion.
            db.LocalBookFiles.Add(new LocalBookFile { Id = 50, AuthorId = 1, AuthorFolder = "Alan Dean Foster", TitleFolder = "Alan Dean Foster - Oshenerth  (retail) (epub)", FullPath = "/lib/A/Alan Dean Foster - Oshenerth  (retail) (epub).epub" });
            // File whose real title IS owned (Codgerspace) → must match the real book, not the junk.
            db.LocalBookFiles.Add(new LocalBookFile { Id = 51, AuthorId = 1, AuthorFolder = "Alan Dean Foster", TitleFolder = "Alan Dean Foster - Codgerspace (retail) (epub)", FullPath = "/lib/A/Alan Dean Foster - Codgerspace (retail) (epub).epub" });
        });
        using var client = factory.CreateClient();

        var sets = await client.GetFromJsonAsync<List<AuthorsController.FileSuggestionSet>>("/api/authors/1/unmatched/suggestions?top=5");

        var oshenerth = sets!.Single(s => s.FileId == 50);
        Assert.DoesNotContain(oshenerth.Candidates, c => c.BookId == 10); // no junk match
        Assert.Empty(oshenerth.Candidates);                               // no real book exists → no suggestion at all

        var codgerspace = sets!.Single(s => s.FileId == 51);
        Assert.Equal(11, codgerspace.Candidates[0].BookId);              // real book wins
        Assert.DoesNotContain(codgerspace.Candidates, c => c.BookId == 10);
    }

    [Fact]
    public async Task SendUnmatchedToUnknown_Moves_File_And_Drops_Row()
    {
        using var factory = new LibraryApiFactory();
        var root = Path.Combine(Path.GetTempPath(), $"thelibrary-unk-{Guid.NewGuid():N}");
        var authorDir = Path.Combine(root, "Wrong Author");
        Directory.CreateDirectory(authorDir);
        var src = Path.Combine(authorDir, "Some Book.epub");
        await File.WriteAllTextAsync(src, "x");
        try
        {
            await SeedAsync(factory, db =>
            {
                db.LibraryLocations.Add(new LibraryLocation { Id = 1, Label = "L", Path = root, Enabled = true, CreatedAt = DateTime.UtcNow });
                db.Authors.Add(new Author { Id = 1, Name = "Wrong Author" });
                db.LocalBookFiles.Add(new LocalBookFile { Id = 50, AuthorId = 1, AuthorFolder = "Wrong Author", TitleFolder = "Some Book", FullPath = src });
            });
            using var client = factory.CreateClient();

            var resp = await client.PostAsync("/api/authors/1/unmatched/50/to-unknown", null);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            Assert.False(File.Exists(src));                                  // moved off the author folder
            Assert.True(Directory.Exists(Path.Combine(root, CalibreScanner.UnknownAuthorFolder))); // into __unknown
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            Assert.Null(await db.LocalBookFiles.FindAsync(50));             // row dropped (re-indexed as untracked next sync)
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteUnmatched_Removes_File_And_Row()
    {
        using var factory = new LibraryApiFactory();
        var root = Path.Combine(Path.GetTempPath(), $"thelibrary-del-{Guid.NewGuid():N}");
        var authorDir = Path.Combine(root, "A");
        Directory.CreateDirectory(authorDir);
        var src = Path.Combine(authorDir, "Junk.epub");
        await File.WriteAllTextAsync(src, "x");
        try
        {
            await SeedAsync(factory, db =>
            {
                db.LibraryLocations.Add(new LibraryLocation { Id = 1, Label = "L", Path = root, Enabled = true, CreatedAt = DateTime.UtcNow });
                db.Authors.Add(new Author { Id = 1, Name = "A" });
                db.LocalBookFiles.Add(new LocalBookFile { Id = 50, AuthorId = 1, AuthorFolder = "A", TitleFolder = "Junk", FullPath = src });
            });
            using var client = factory.CreateClient();

            var resp = await client.DeleteAsync("/api/authors/1/unmatched/50");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            Assert.False(File.Exists(src));
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            Assert.Null(await db.LocalBookFiles.FindAsync(50));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Suggestions_Skip_Foreign_And_Suppressed_Books()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "A" });
            db.Books.AddRange(
                new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W", Title = "Midnight", NormalizedTitle = TitleNormalizer.Normalize("Midnight"), Foreign = true, Suppressed = true },
                new Book { Id = 11, AuthorId = 1, OpenLibraryWorkKey = "OL11W", Title = "Midnight", NormalizedTitle = TitleNormalizer.Normalize("Midnight") });
            db.LocalBookFiles.Add(new LocalBookFile { Id = 50, AuthorId = 1, AuthorFolder = "A", TitleFolder = "Midnight", FullPath = "/lib/A/Midnight.epub" });
        });
        using var client = factory.CreateClient();

        var sets = await client.GetFromJsonAsync<List<AuthorsController.FileSuggestionSet>>("/api/authors/1/unmatched/suggestions?top=5");
        var s = Assert.Single(sets!);
        Assert.DoesNotContain(s.Candidates, c => c.BookId == 10); // foreign+suppressed excluded
        Assert.Contains(s.Candidates, c => c.BookId == 11);
    }

    [Fact]
    public async Task AuthorDetail_Unmatched_Hides_Folder_And_Empty_Rows()
    {
        using var factory = new LibraryApiFactory();
        await SeedAsync(factory, db =>
        {
            db.Authors.Add(new Author { Id = 1, Name = "David A. Gemmell", CalibreFolderName = "David A. Gemmell_OL2644937A" });
            db.LocalBookFiles.AddRange(
                // A real flat-file row → shown.
                new LocalBookFile { Id = 50, AuthorId = 1, AuthorFolder = "David A. Gemmell_OL2644937A", TitleFolder = "Legend", FullPath = "/Books/Collection/David A. Gemmell_OL2644937A/Legend.epub" },
                // A folder-shaped row with no ebook extension and nothing on disk → hidden.
                new LocalBookFile { Id = 51, AuthorId = 1, AuthorFolder = "David A. Gemmell_OL2644937A", TitleFolder = "David Gemmell", FullPath = "/Books/Collection/David A. Gemmell_OL2644937A/David Gemmell" },
                // The bare author-folder row with an empty title → hidden.
                new LocalBookFile { Id = 52, AuthorId = 1, AuthorFolder = "David A. Gemmell_OL2644937A", TitleFolder = "", FullPath = "/Books/Collection/David A. Gemmell" });
        });
        using var client = factory.CreateClient();

        var detail = await client.GetFromJsonAsync<AuthorsController.AuthorDetail>("/api/authors/1");
        var ids = detail!.UnmatchedLocal.Select(u => u.Id).ToList();
        Assert.Equal(new[] { 50 }, ids); // only the real ebook file
    }

    private static async Task SeedAsync(LibraryApiFactory factory, Action<LibraryDbContext> seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }
}

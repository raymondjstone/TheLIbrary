using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheLibrary.Server.Controllers;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

public class BookConversionControllerTests
{
    // Fake ebook-convert: succeeds and "produces" the requested output file in the
    // fake filesystem (ConvertToEpubAsync verifies the output exists).
    private sealed class StubRunner(FakeFileSystem fs) : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(ProcessStartInfo psi, TimeSpan timeout, CancellationToken ct)
        {
            fs.ExistingFiles.Add(psi.ArgumentList[1]); // out path
            return Task.FromResult(new ProcessRunResult(0, "", ""));
        }
    }

    private static LibraryDbContext NewDb(string name)
        => new(new DbContextOptionsBuilder<LibraryDbContext>().UseInMemoryDatabase(name).Options);

    [Fact]
    public async Task ConvertToEpub_Converts_Source_And_Tracks_New_File()
    {
        var name = "convert-" + Guid.NewGuid().ToString("N");
        var fs = new FakeFileSystem();
        fs.AddFile("/lib/Auth/book.pdf");

        await using (var seed = NewDb(name))
        {
            seed.Authors.Add(new Author { Id = 1, Name = "Auth" });
            seed.Books.Add(new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W", Title = "Book", NormalizedTitle = "book" });
            seed.LocalBookFiles.Add(new LocalBookFile { Id = 1, BookId = 10, AuthorId = 1, AuthorFolder = "Auth", FullPath = "/lib/Auth/book.pdf", ModifiedAt = DateTime.UtcNow });
            await seed.SaveChangesAsync();
        }

        var converter = new CalibreConverter(
            Options.Create(new CalibreOptions { EbookConvert = "ebook-convert" }),
            fs, new StubRunner(fs), NullLogger<CalibreConverter>.Instance);

        await using var db = NewDb(name);
        var controller = new BookConversionController(db, fs, converter);
        var result = await controller.ConvertToEpub(10, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<BookConversionController.ConvertResult>(ok.Value);
        Assert.True(body.Converted);
        Assert.EndsWith("book.epub", body.Path);

        await using var verify = NewDb(name);
        Assert.True(await verify.LocalBookFiles.AnyAsync(f => f.BookId == 10 && f.FullPath.EndsWith(".epub")));
    }

    [Fact]
    public async Task ConvertToEpub_NoOp_When_Epub_Exists()
    {
        var name = "convert2-" + Guid.NewGuid().ToString("N");
        var fs = new FakeFileSystem();
        fs.AddFile("/lib/Auth/book.epub");

        await using (var seed = NewDb(name))
        {
            seed.Authors.Add(new Author { Id = 1, Name = "Auth" });
            seed.Books.Add(new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W", Title = "Book", NormalizedTitle = "book" });
            seed.LocalBookFiles.Add(new LocalBookFile { Id = 1, BookId = 10, AuthorId = 1, AuthorFolder = "Auth", FullPath = "/lib/Auth/book.epub", ModifiedAt = DateTime.UtcNow });
            await seed.SaveChangesAsync();
        }

        var converter = new CalibreConverter(
            Options.Create(new CalibreOptions { EbookConvert = "ebook-convert" }),
            fs, new StubRunner(fs), NullLogger<CalibreConverter>.Instance);

        await using var db = NewDb(name);
        var result = await new BookConversionController(db, fs, converter).ConvertToEpub(10, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.False(((BookConversionController.ConvertResult)ok.Value!).Converted);
    }
}

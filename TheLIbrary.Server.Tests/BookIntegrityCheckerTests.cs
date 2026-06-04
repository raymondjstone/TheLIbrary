using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

public class BookIntegrityCheckerTests
{
    [Fact]
    public async Task CheckAsync_Healthy_Epub_Is_Ok()
    {
        var fs = new FakeFileSystem();
        fs.AddFile("/books/good.epub", TestEpub.Build(("c.xhtml", TestEpub.HtmlWithText(30_000))));
        var sut = CreateChecker(fs, configured: false);

        var result = await sut.CheckAsync("/books/good.epub", CancellationToken.None);

        Assert.Equal(IntegrityStatus.Ok, result.Status);
        Assert.True(result.Pages >= BookIntegrityChecker.MinPages);
    }

    [Fact]
    public async Task CheckAsync_Short_Epub_Is_Damaged()
    {
        var fs = new FakeFileSystem();
        fs.AddFile("/books/tiny.epub", TestEpub.Build(("c.xhtml", TestEpub.HtmlWithText(2_000))));
        var sut = CreateChecker(fs, configured: false);

        var result = await sut.CheckAsync("/books/tiny.epub", CancellationToken.None);

        Assert.Equal(IntegrityStatus.Damaged, result.Status);
        Assert.Contains("at least 20", result.Error);
    }

    [Fact]
    public async Task CheckAsync_Corrupt_Epub_Is_Damaged()
    {
        var fs = new FakeFileSystem();
        fs.AddFile("/books/broken.epub", new byte[] { 0, 1, 2, 3, 4 });
        var sut = CreateChecker(fs, configured: false);

        var result = await sut.CheckAsync("/books/broken.epub", CancellationToken.None);

        Assert.Equal(IntegrityStatus.Damaged, result.Status);
    }

    [Fact]
    public async Task CheckAsync_Unreadable_Pdf_Is_Damaged()
    {
        var fs = new FakeFileSystem();
        fs.AddFile("/books/broken.pdf", new byte[] { 0, 1, 2, 3 });
        var sut = CreateChecker(fs, configured: false);

        var result = await sut.CheckAsync("/books/broken.pdf", CancellationToken.None);

        Assert.Equal(IntegrityStatus.Damaged, result.Status);
        Assert.Contains("PDF", result.Error);
    }

    [Fact]
    public async Task CheckAsync_Missing_File_Is_Damaged()
    {
        var sut = CreateChecker(new FakeFileSystem(), configured: false);

        var result = await sut.CheckAsync("/books/gone.epub", CancellationToken.None);

        Assert.Equal(IntegrityStatus.Damaged, result.Status);
        Assert.Contains("no longer exists", result.Error);
    }

    [Fact]
    public async Task CheckAsync_NonNative_Format_Is_Skipped_When_Calibre_Not_Configured()
    {
        var fs = new FakeFileSystem();
        fs.AddFile("/books/book.mobi", new byte[] { 1, 2, 3 });
        var sut = CreateChecker(fs, configured: false);

        var result = await sut.CheckAsync("/books/book.mobi", CancellationToken.None);

        Assert.Equal(IntegrityStatus.Skipped, result.Status);
    }

    [Fact]
    public async Task CheckAsync_NonNative_Format_Is_Converted_Then_Inspected()
    {
        var fs = new FakeFileSystem();
        fs.AddFile("/books/book.mobi", new byte[] { 1, 2, 3 });
        // The fake ebook-convert "produces" a healthy EPUB at its output path.
        var converter = CreateConverter(fs, startInfo =>
            fs.AddFile(startInfo.ArgumentList[1], TestEpub.Build(("c.xhtml", TestEpub.HtmlWithText(30_000)))));
        var sut = new BookIntegrityChecker(converter, fs, NullLogger<BookIntegrityChecker>.Instance);

        var result = await sut.CheckAsync("/books/book.mobi", CancellationToken.None);

        Assert.Equal(IntegrityStatus.Ok, result.Status);
    }

    [Fact]
    public async Task CheckAsync_NonNative_Conversion_Failure_Is_Damaged()
    {
        var fs = new FakeFileSystem();
        fs.AddFile("/books/book.mobi", new byte[] { 1, 2, 3 });
        // ebook-convert exits non-zero → CalibreConversionException → damaged.
        var converter = CreateConverter(fs, _ => { }, new ProcessRunResult(1, "", "boom"));
        var sut = new BookIntegrityChecker(converter, fs, NullLogger<BookIntegrityChecker>.Instance);

        var result = await sut.CheckAsync("/books/book.mobi", CancellationToken.None);

        Assert.Equal(IntegrityStatus.Damaged, result.Status);
        Assert.Contains("convert", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/x/y.epub", true)]
    [InlineData("/x/y.PDF", true)]
    [InlineData("/x/y.mobi", true)]
    [InlineData("/x/y.jpg", false)]
    [InlineData("/x/y", false)]
    public void IsEbook_Recognises_Ebook_Extensions(string path, bool expected)
        => Assert.Equal(expected, BookIntegrityChecker.IsEbook(path));

    private static BookIntegrityChecker CreateChecker(FakeFileSystem fs, bool configured)
        => new(CreateConverter(fs, _ => { }, configured: configured), fs, NullLogger<BookIntegrityChecker>.Instance);

    private static CalibreConverter CreateConverter(
        FakeFileSystem fs,
        Action<System.Diagnostics.ProcessStartInfo> onRun,
        ProcessRunResult? result = null,
        bool configured = true)
        => new(
            Options.Create(new CalibreOptions { EbookConvert = configured ? "ebook-convert" : null }),
            fs,
            new FakeProcessRunner(onRun, result),
            NullLogger<CalibreConverter>.Instance);

    private sealed class FakeProcessRunner(Action<System.Diagnostics.ProcessStartInfo> onRun, ProcessRunResult? result)
        : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(System.Diagnostics.ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken ct)
        {
            onRun(startInfo);
            return Task.FromResult(result ?? new ProcessRunResult(0, "", ""));
        }
    }
}

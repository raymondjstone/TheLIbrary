using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

public class CalibreConverterTests
{
    [Fact]
    public async Task ConvertToEpubAsync_Throws_When_Source_Is_Missing()
    {
        var fs = new FakeFileSystem();
        var sut = CreateConverter(fs, new FakeProcessRunner());

        var ex = await Assert.ThrowsAsync<CalibreConversionException>(() => sut.ConvertToEpubAsync("C:\\missing.mobi", CancellationToken.None));
        Assert.Contains("Source file not found", ex.Message);
    }

    [Fact]
    public async Task ConvertToEpubAsync_Throws_When_EbookConvert_Not_Configured()
    {
        var fs = new FakeFileSystem();
        fs.AddFile("C:\\book.mobi", [1, 2, 3]);
        var sut = new CalibreConverter(Options.Create(new CalibreOptions()), fs, new FakeProcessRunner(), NullLogger<CalibreConverter>.Instance);

        var ex = await Assert.ThrowsAsync<CalibreConversionException>(() => sut.ConvertToEpubAsync("C:\\book.mobi", CancellationToken.None));
        Assert.Contains("not configured", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConvertToEpubAsync_Throws_When_Process_Times_Out()
    {
        var fs = new FakeFileSystem();
        fs.AddFile("C:\\book.mobi", [1, 2, 3]);
        var sut = CreateConverter(fs, new TimeoutProcessRunner());

        var ex = await Assert.ThrowsAsync<CalibreConversionException>(() => sut.ConvertToEpubAsync("C:\\book.mobi", CancellationToken.None));
        Assert.Contains("timeout", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConvertToEpubAsync_Throws_When_Process_Fails()
    {
        var fs = new FakeFileSystem();
        fs.AddFile("C:\\book.mobi", [1, 2, 3]);
        var sut = CreateConverter(fs, new FakeProcessRunner(new ProcessRunResult(2, "", "bad things happened")));

        var ex = await Assert.ThrowsAsync<CalibreConversionException>(() => sut.ConvertToEpubAsync("C:\\book.mobi", CancellationToken.None));
        Assert.Contains("exited 2", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConvertToEpubAsync_Returns_Output_When_Process_Succeeds()
    {
        var fs = new FakeFileSystem();
        fs.AddFile("C:\\book.mobi", [1, 2, 3]);
        var runner = new FakeProcessRunner(onRun: startInfo => fs.AddFile(startInfo.ArgumentList[1], [9, 9, 9]));
        var sut = CreateConverter(fs, runner);

        var path = await sut.ConvertToEpubAsync("C:\\book.mobi", CancellationToken.None);

        Assert.EndsWith(".epub", path, StringComparison.OrdinalIgnoreCase);
        Assert.True(fs.FileExists(path));
    }

    private static CalibreConverter CreateConverter(FakeFileSystem fs, IProcessRunner runner)
        => new(Options.Create(new CalibreOptions { EbookConvert = "ebook-convert" }), fs, runner, NullLogger<CalibreConverter>.Instance);

    private sealed class FakeProcessRunner(ProcessRunResult? result = null, Action<System.Diagnostics.ProcessStartInfo>? onRun = null) : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(System.Diagnostics.ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken ct)
        {
            onRun?.Invoke(startInfo);
            return Task.FromResult(result ?? new ProcessRunResult(0, "", ""));
        }
    }

    private sealed class TimeoutProcessRunner : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(System.Diagnostics.ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken ct)
            => throw new ProcessRunTimeoutException();
    }
}

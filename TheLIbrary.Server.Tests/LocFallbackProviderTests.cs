using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

public class LocFallbackProviderTests
{
    private sealed class FakeFactory : IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        public FakeFactory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => _handler = handler;
        public HttpClient CreateClient(string name) =>
            new(new TestHttpMessageHandler(_handler)) { BaseAddress = new Uri("http://lx2.loc.gov:210/") };
    }

    private static LocFallbackProvider Provider(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        => new(new FakeFactory(handler), NullLogger<LocFallbackProvider>.Instance);

    // A real-shaped SRU/MODS response (trimmed from the live LC endpoint).
    private const string OneRecordMods = """
        <?xml version="1.0"?>
        <zs:searchRetrieveResponse xmlns:zs="http://www.loc.gov/zing/srw/">
          <zs:version>1.1</zs:version><zs:numberOfRecords>1</zs:numberOfRecords>
          <zs:records><zs:record><zs:recordData>
            <mods xmlns="http://www.loc.gov/mods/v3" version="3.8">
              <titleInfo><nonSort xml:space="preserve">The </nonSort><title>great Gatsby</title></titleInfo>
              <name type="personal" usage="primary">
                <namePart>Fitzgerald, F. Scott (Francis Scott),</namePart>
                <namePart type="date">1896-1940</namePart>
              </name>
              <originInfo><dateIssued encoding="marc">2018</dateIssued></originInfo>
            </mods>
          </zs:recordData></zs:record></zs:records>
        </zs:searchRetrieveResponse>
        """;

    private const string ZeroRecords = """
        <?xml version="1.0"?>
        <zs:searchRetrieveResponse xmlns:zs="http://www.loc.gov/zing/srw/">
          <zs:version>1.1</zs:version><zs:numberOfRecords>0</zs:numberOfRecords>
        </zs:searchRetrieveResponse>
        """;

    [Fact]
    public async Task Skipped_When_Not_Enabled()
    {
        var p = Provider((_, _) => throw new Exception("should not be called"));
        Assert.Equal(IsbnLookupStatus.Skipped, (await p.LookupAsync("9780743273565", "", default)).Status);
    }

    [Fact]
    public async Task Parses_Title_Author_Year_From_Mods()
    {
        string? url = null;
        var p = Provider((req, _) =>
        {
            url = req.RequestUri!.ToString();
            return Task.FromResult(TestHttpMessageHandler.Json(OneRecordMods));
        });

        var r = await p.LookupAsync("9780743273565", "true", default);

        Assert.Equal(IsbnLookupStatus.Hit, r.Status);
        Assert.Equal("The great Gatsby", r.Title);              // nonSort + title, whitespace collapsed
        Assert.Equal("Fitzgerald, F. Scott", r.Author);         // parenthetical + trailing comma stripped
        Assert.Equal(2018, r.FirstPublishYear);
        Assert.Contains("bath.isbn", url);                      // queried by ISBN
    }

    [Fact]
    public async Task Miss_When_No_Records()
    {
        var p = Provider((_, _) => Task.FromResult(TestHttpMessageHandler.Json(ZeroRecords)));
        Assert.Equal(IsbnLookupStatus.Miss, (await p.LookupAsync("9780000000000", "true", default)).Status);
    }

    [Fact]
    public async Task Unavailable_On_Http_Error()
    {
        var p = Provider((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        Assert.Equal(IsbnLookupStatus.Unavailable, (await p.LookupAsync("9780743273565", "true", default)).Status);
    }
}

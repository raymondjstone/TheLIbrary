using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

public class IsbndbFallbackProviderTests
{
    // Minimal IHttpClientFactory that hands back a client wired to a scripted handler.
    private sealed class FakeFactory : IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        public FakeFactory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => _handler = handler;
        public HttpClient CreateClient(string name) =>
            new(new TestHttpMessageHandler(_handler)) { BaseAddress = new Uri("https://api2.isbndb.com/") };
    }

    private static IsbndbFallbackProvider Provider(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        => new(new FakeFactory(handler), NullLogger<IsbndbFallbackProvider>.Instance);

    [Fact]
    public async Task Skipped_When_No_Credential()
    {
        var p = Provider((_, _) => throw new Exception("should not be called"));
        var r = await p.LookupAsync("9798636352273", "  ", default);
        Assert.Equal(IsbnLookupStatus.Skipped, r.Status);
    }

    [Fact]
    public async Task Hit_Parses_Title_First_Author_And_Year()
    {
        string? auth = null;
        var p = Provider((req, _) =>
        {
            auth = req.Headers.TryGetValues("Authorization", out var v) ? string.Join("", v) : null;
            return Task.FromResult(TestHttpMessageHandler.Json("""
                {"book":{"title":"Indie Thriller","authors":["A. Author","B. Second"],"date_published":"2022-03-14"}}
                """));
        });

        var r = await p.LookupAsync("9798636352273", "db-key", default);

        Assert.Equal(IsbnLookupStatus.Hit, r.Status);
        Assert.Equal("Indie Thriller", r.Title);
        Assert.Equal("A. Author", r.Author);
        Assert.Equal(2022, r.FirstPublishYear);
        Assert.Equal("db-key", auth);     // bare key, no "Bearer"
    }

    [Fact]
    public async Task Miss_On_404()
    {
        var p = Provider((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Equal(IsbnLookupStatus.Miss, (await p.LookupAsync("9780000000000", "k", default)).Status);
    }

    [Fact]
    public async Task Skipped_On_Unauthorized_Key()
    {
        var p = Provider((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        Assert.Equal(IsbnLookupStatus.Skipped, (await p.LookupAsync("9780000000000", "bad", default)).Status);
    }

    [Fact]
    public async Task Unavailable_On_429()
    {
        var p = Provider((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        Assert.Equal(IsbnLookupStatus.Unavailable, (await p.LookupAsync("9780000000000", "k", default)).Status);
    }
}

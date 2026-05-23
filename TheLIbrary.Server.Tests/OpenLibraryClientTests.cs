using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

public class OpenLibraryClientTests
{
    [Fact]
    public async Task SearchAuthorsAsync_Parses_Response()
    {
        var settings = new OpenLibrarySettings(new NoopScopeFactory());
        var limiter = new OpenLibraryRateLimiter(settings);
        var http = new HttpClient(new TestHttpMessageHandler((request, _) =>
        {
            Assert.Contains("search/authors.json", request.RequestUri!.ToString());
            return Task.FromResult(TestHttpMessageHandler.Json("""
                {"docs":[{"key":"OL1A","name":"Author One"}]}
                """));
        }))
        {
            BaseAddress = new Uri("https://openlibrary.org/")
        };
        var client = new OpenLibraryClient(http, limiter, settings, NullLogger<OpenLibraryClient>.Instance);

        var result = await client.SearchAuthorsAsync("Author One", CancellationToken.None);

        Assert.Equal("OL1A", Assert.Single(result!.Docs).Key);
    }

    [Fact]
    public async Task FetchAuthorMergesAsync_Returns_Empty_On_NotFound()
    {
        var settings = new OpenLibrarySettings(new NoopScopeFactory());
        var limiter = new OpenLibraryRateLimiter(settings);
        var http = new HttpClient(new TestHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound))))
        {
            BaseAddress = new Uri("https://openlibrary.org/")
        };
        var client = new OpenLibraryClient(http, limiter, settings, NullLogger<OpenLibraryClient>.Instance);

        var result = await client.FetchAuthorMergesAsync(new DateOnly(2025, 1, 1), CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SearchAuthorsAsync_Throws_After_Retries_On_Server_Error()
    {
        var settings = new OpenLibrarySettings(new NoopScopeFactory());
        var limiter = new OpenLibraryRateLimiter(settings);
        var attempts = 0;
        var http = new HttpClient(new TestHttpMessageHandler((_, _) =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        }))
        {
            BaseAddress = new Uri("https://openlibrary.org/")
        };
        var client = new OpenLibraryClient(http, limiter, settings, NullLogger<OpenLibraryClient>.Instance);

        var ex = await Record.ExceptionAsync(() => client.SearchAuthorsAsync("broken", CancellationToken.None));
        Assert.NotNull(ex);
        Assert.Equal(5, attempts);
    }
}

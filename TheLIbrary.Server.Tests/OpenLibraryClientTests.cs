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
    public async Task SearchWorksAsync_Includes_Author_Filter_When_Provided()
    {
        string? capturedUrl = null;
        var client = CreateClient((request, _) =>
        {
            capturedUrl = request.RequestUri!.ToString();
            return Task.FromResult(TestHttpMessageHandler.Json("""
                {"numFound":1,"docs":[{"key":"/works/OL9W","title":"Dune"}]}
                """));
        });

        var result = await client.SearchWorksAsync("Dune", "Frank Herbert", CancellationToken.None);

        Assert.Equal("/works/OL9W", Assert.Single(result!.Docs).Key);
        Assert.Contains("search.json", capturedUrl);
        Assert.Contains("title=Dune", capturedUrl);
        Assert.Contains("author=Frank+Herbert", capturedUrl);
    }

    [Fact]
    public async Task SearchWorksAsync_Omits_Author_Filter_When_Blank()
    {
        string? capturedUrl = null;
        var client = CreateClient((request, _) =>
        {
            capturedUrl = request.RequestUri!.ToString();
            return Task.FromResult(TestHttpMessageHandler.Json("""{"numFound":0,"docs":[]}"""));
        });

        await client.SearchWorksAsync("Dune", "   ", CancellationToken.None);

        Assert.DoesNotContain("author=", capturedUrl);
    }

    [Fact]
    public async Task FetchWorkAsync_Strips_Works_Prefix_From_Key()
    {
        string? capturedPath = null;
        var client = CreateClient((request, _) =>
        {
            capturedPath = request.RequestUri!.AbsolutePath;
            return Task.FromResult(TestHttpMessageHandler.Json("""{"key":"/works/OL42W"}"""));
        });

        var result = await client.FetchWorkAsync("/works/OL42W", CancellationToken.None);

        Assert.Equal("/works/OL42W", result!.Key);
        // The "/works/" prefix must not be doubled up in the request path.
        Assert.Equal("/works/OL42W.json", capturedPath);
    }

    [Fact]
    public async Task FetchAuthorAsync_Returns_Null_On_NotFound()
    {
        var client = CreateClient((_, _) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)));

        var result = await client.FetchAuthorAsync("OL1A", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task DownloadCoverBytesAsync_Returns_Bytes_On_Success()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        var client = CreateClient((request, _) =>
        {
            Assert.Contains("covers.openlibrary.org", request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
        });

        var bytes = await client.DownloadCoverBytesAsync(12345, CancellationToken.None);

        Assert.Equal(payload, bytes);
    }

    [Fact]
    public async Task DownloadCoverBytesAsync_Returns_Null_On_Failure()
    {
        var client = CreateClient((_, _) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)));

        var bytes = await client.DownloadCoverBytesAsync(999, CancellationToken.None);

        Assert.Null(bytes);
    }

    [Fact]
    public async Task GetEnglishWorksAsync_Pages_Through_Results_Until_NumFound_Reached()
    {
        // numFound=150 with a 100-item page size means two pages: 100 then 50.
        var pageOneDocs = string.Join(",", Enumerable.Range(0, 100).Select(i => $$"""{"key":"/works/OL{{i}}W"}"""));
        var pageTwoDocs = string.Join(",", Enumerable.Range(100, 50).Select(i => $$"""{"key":"/works/OL{{i}}W"}"""));
        var requestedPages = new List<string>();
        var client = CreateClient((request, _) =>
        {
            var url = request.RequestUri!.ToString();
            Assert.Contains("language=eng", url);
            var page = url.Contains("page=2") ? "2" : "1";
            requestedPages.Add(page);
            var docs = page == "1" ? pageOneDocs : pageTwoDocs;
            return Task.FromResult(TestHttpMessageHandler.Json($$"""{"numFound":150,"docs":[{{docs}}]}"""));
        });

        var all = new List<WorkSearchDoc>();
        await foreach (var doc in client.GetEnglishWorksAsync("OL1A", CancellationToken.None))
            all.Add(doc);

        Assert.Equal(150, all.Count);
        Assert.Equal(new[] { "1", "2" }, requestedPages);
    }

    [Fact]
    public async Task GetAllWorksAsync_Stops_On_Short_Page_Without_Language_Filter()
    {
        string? capturedUrl = null;
        var client = CreateClient((request, _) =>
        {
            capturedUrl = request.RequestUri!.ToString();
            return Task.FromResult(TestHttpMessageHandler.Json("""
                {"numFound":3,"docs":[{"key":"/works/OL1W"},{"key":"/works/OL2W"},{"key":"/works/OL3W"}]}
                """));
        });

        var all = new List<WorkSearchDoc>();
        await foreach (var doc in client.GetAllWorksAsync("OL1A", CancellationToken.None))
            all.Add(doc);

        Assert.Equal(3, all.Count);
        Assert.DoesNotContain("language=", capturedUrl);
    }

    [Fact]
    public async Task FetchAuthorMergesAsync_Parses_Merge_Entries()
    {
        var client = CreateClient((_, _) => Task.FromResult(TestHttpMessageHandler.Json("""
            [{"kind":"merge-authors","data":{"master":"/authors/OL1A","duplicates":["/authors/OL2A"]}}]
            """)));

        var result = await client.FetchAuthorMergesAsync(new DateOnly(2025, 3, 4), CancellationToken.None);

        Assert.Equal("/authors/OL1A", Assert.Single(result).Data!.Master);
    }

    private static OpenLibraryClient CreateClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        var settings = new OpenLibrarySettings(new NoopScopeFactory());
        var limiter = new OpenLibraryRateLimiter(settings);
        var http = new HttpClient(new TestHttpMessageHandler(handler))
        {
            BaseAddress = new Uri("https://openlibrary.org/")
        };
        return new OpenLibraryClient(http, limiter, settings, NullLogger<OpenLibraryClient>.Instance);
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

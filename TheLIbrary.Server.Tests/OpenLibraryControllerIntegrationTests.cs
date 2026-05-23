using System.Net;
using System.Net.Http.Json;
using TheLibrary.Server.Controllers;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

[Collection("Integration")]
public class OpenLibraryControllerIntegrationTests
{
    [Fact]
    public async Task SearchAuthors_Returns_BadRequest_For_Blank_Query_In_Api_Pipeline()
    {
        using var factory = new LibraryApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/openlibrary/search-authors?q=%20");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SearchAuthors_Maps_Docs_From_OpenLibrary()
    {
        using var factory = new LibraryApiFactory((request, _) =>
        {
            Assert.Contains("search/authors.json", request.RequestUri!.ToString());
            return Task.FromResult(TestHttpMessageHandler.Json("""
                {"docs":[{"key":"OL1A","name":"Terry Brooks","top_work":"Magic Kingdom","work_count":42,"birth_date":"1944"}]}
                """));
        });
        using var client = factory.CreateClient();

        var result = await client.GetFromJsonAsync<List<OpenLibraryController.AuthorSearchRow>>("/api/openlibrary/search-authors?q=Terry%20Brooks");

        var row = Assert.Single(result!);
        Assert.Equal("OL1A", row.Key);
        Assert.Equal("Terry Brooks", row.Name);
        Assert.Equal("Magic Kingdom", row.TopWork);
        Assert.Equal(42, row.WorkCount);
    }

    [Fact]
    public async Task SearchAuthors_Returns_503_When_OpenLibrary_Request_Fails()
    {
        using var factory = new LibraryApiFactory((_, _) => throw new OpenLibraryRequestFailedException("search/authors.json?q=broken"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/openlibrary/search-authors?q=broken");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}

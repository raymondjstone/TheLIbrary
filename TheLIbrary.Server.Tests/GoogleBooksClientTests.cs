using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

public class GoogleBooksClientTests
{
    private static GoogleBooksClient Client(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
        GoogleBooksRateLimiter? limiter = null)
    {
        var http = new HttpClient(new TestHttpMessageHandler(handler));
        return new GoogleBooksClient(http, limiter ?? new GoogleBooksRateLimiter(), NullLogger<GoogleBooksClient>.Instance);
    }

    [Fact]
    public async Task ResolveByIsbn_Returns_Title_Author_Year_From_First_Volume()
    {
        string? requested = null;
        var client = Client((req, _) =>
        {
            requested = req.RequestUri!.ToString();
            return Task.FromResult(TestHttpMessageHandler.Json("""
                {"totalItems":1,"items":[{"volumeInfo":{
                    "title":"The Indie Novel","authors":["A. Self-Publisher","Second Name"],
                    "publishedDate":"2021-06-01"}}]}
                """));
        });

        var info = await client.ResolveByIsbnAsync("9798636352273", "test-key", default);

        Assert.NotNull(info);
        Assert.Equal("The Indie Novel", info!.Title);
        Assert.Equal("A. Self-Publisher", info.Author);  // first author only
        Assert.Equal(2021, info.FirstPublishYear);
        Assert.Contains("isbn:9798636352273", requested);
        Assert.Contains("key=test-key", requested);       // key passed through
    }

    [Fact]
    public async Task ResolveByIsbn_Returns_Null_When_No_Items()
    {
        var client = Client((_, _) =>
            Task.FromResult(TestHttpMessageHandler.Json("""{"totalItems":0}""")));

        Assert.Null(await client.ResolveByIsbnAsync("9780000000000", "k", default));
    }

    [Fact]
    public async Task ResolveByIsbn_Throws_Quota_On_429_And_Latches_The_Day()
    {
        var limiter = new GoogleBooksRateLimiter();
        var calls = 0;
        var client = Client((_, _) =>
        {
            calls++;
            return Task.FromResult(TestHttpMessageHandler.Json("""{"error":"rate"}""", HttpStatusCode.TooManyRequests));
        }, limiter);

        // First call hits Google, gets 429 -> quota exception + latch set.
        await Assert.ThrowsAsync<GoogleBooksQuotaExceededException>(
            () => client.ResolveByIsbnAsync("9780000000001", "k", default));
        Assert.True(limiter.IsExhaustedToday);

        // Second call short-circuits on the latch — no further HTTP call is made.
        await Assert.ThrowsAsync<GoogleBooksQuotaExceededException>(
            () => client.ResolveByIsbnAsync("9780000000002", "k", default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ResolveByIsbn_Throws_Http_On_NonQuota_Error()
    {
        var client = Client((_, _) =>
            Task.FromResult(TestHttpMessageHandler.Json("""{"error":"boom"}""", HttpStatusCode.InternalServerError)));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ResolveByIsbnAsync("9780000000000", "k", default));
    }

    // Regression test for the "retry-isbn-misses sticks after 1 item, forever" bug:
    // Google Cloud API quotas (Books API included) reset at midnight PACIFIC time,
    // not UTC — confirmed live against Google's own 429 response. The rate limiter
    // must track the Pacific-time day for the exhaustion latch, not the UTC day; the
    // earlier UTC-based version cleared the latch at 00:00 UTC while Google's quota
    // stayed spent until the real reset (~07:00 UTC PDT / ~08:00 UTC PST), so a job
    // running in that gap always "discovered" the still-exhausted quota fresh, one
    // wasted call at a time, day after day.
    [Fact]
    public void PacificToday_Tracks_The_Pacific_Time_Day_Not_Utc()
    {
        var pacificZone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        var offset = pacificZone.GetUtcOffset(DateTime.UtcNow);

        // Sanity: this really is Pacific time (-7 PDT or -8 PST), not accidentally
        // UTC (0) or some other zone that would silently reintroduce the bug.
        Assert.True(offset == TimeSpan.FromHours(-7) || offset == TimeSpan.FromHours(-8),
            $"expected Pacific UTC offset (-7 PDT or -8 PST), got {offset}");

        var expected = DateOnly.FromDateTime(DateTime.UtcNow.Add(offset));
        Assert.Equal(expected, GoogleBooksRateLimiter.PacificToday());
    }

    [Fact]
    public void MarkExhausted_And_IsExhaustedToday_Agree_On_The_Pacific_Day()
    {
        var limiter = new GoogleBooksRateLimiter();
        Assert.False(limiter.IsExhaustedToday);
        limiter.MarkExhausted();
        Assert.True(limiter.IsExhaustedToday);
    }
}

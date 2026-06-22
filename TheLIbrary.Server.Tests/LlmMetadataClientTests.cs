using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Data;
using TheLibrary.Server.Services.Llm;
using TheLibrary.Server.Services.Scheduling;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

public sealed class LlmMetadataClientTests
{
    private static LlmMetadataClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        => new(new HttpClient(new TestHttpMessageHandler(handler)) { BaseAddress = new Uri("https://example.test/") },
               NullLogger<LlmMetadataClient>.Instance);

    private static readonly LlmSignals Signals = new("Zero Sight - B. Justin Shier.mobi", null, null, null, "opening text");

    [Fact]
    public async Task Parses_Anthropic_Messages_Response()
    {
        string? hitUrl = null;
        var client = Client((req, _) =>
        {
            hitUrl = req.RequestUri!.AbsolutePath;
            Assert.True(req.Headers.Contains("x-api-key"));
            return Task.FromResult(TestHttpMessageHandler.Json(
                """{"content":[{"type":"text","text":"{\"title\":\"Zero Sight\",\"author\":\"B. Justin Shier\",\"isbn\":\"9780983500001\",\"confidence\":0.9}"}]}"""));
        });
        var cfg = new LlmConfig(true, "anthropic", "k", "claude-haiku-4-5-20251001", "https://example.test", 50, 500);

        var guess = await client.IdentifyAsync(cfg, Signals, CancellationToken.None);

        Assert.Equal("/v1/messages", hitUrl);
        Assert.Equal("Zero Sight", guess!.Title);
        Assert.Equal("B. Justin Shier", guess.Author);
        Assert.Equal("9780983500001", guess.Isbn);
    }

    [Fact]
    public async Task Parses_OpenAi_ChatCompletions_Response()
    {
        string? hitUrl = null;
        var client = Client((req, _) =>
        {
            hitUrl = req.RequestUri!.AbsolutePath;
            Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
            return Task.FromResult(TestHttpMessageHandler.Json(
                """{"choices":[{"message":{"content":"{\"title\":\"Wedding Fever\",\"author\":\"Ryland Reynolds\",\"confidence\":0.8}"}}]}"""));
        });
        var cfg = new LlmConfig(true, "openai", "k", "gpt-4o-mini", "https://example.test", 50, 500);

        var guess = await client.IdentifyAsync(cfg, Signals, CancellationToken.None);

        Assert.Equal("/v1/chat/completions", hitUrl);
        Assert.Equal("Wedding Fever", guess!.Title);
        Assert.Equal("Ryland Reynolds", guess.Author);
    }

    [Fact]
    public async Task Drops_Unknown_Author_Placeholder()
    {
        var client = Client((_, _) => Task.FromResult(TestHttpMessageHandler.Json(
            """{"content":[{"type":"text","text":"{\"title\":\"X\",\"author\":\"Unknown\",\"confidence\":0.2}"}]}""")));
        var cfg = new LlmConfig(true, "anthropic", "k", "m", "https://example.test", 50, 500);

        var guess = await client.IdentifyAsync(cfg, Signals, CancellationToken.None);

        Assert.Equal("X", guess!.Title);
        Assert.Null(guess.Author);
    }

    [Fact]
    public async Task Not_Ready_Returns_Null_Without_Calling()
    {
        var called = false;
        var client = Client((_, _) => { called = true; return Task.FromResult(TestHttpMessageHandler.Json("{}")); });
        var cfg = new LlmConfig(Enabled: true, "anthropic", ApiKey: null, "m", "https://example.test", 50, 500);

        Assert.Null(await client.IdentifyAsync(cfg, Signals, CancellationToken.None));
        Assert.False(called);
    }

    [Fact]
    public async Task Job_NoOps_When_Disabled()
    {
        var dbName = "llm-" + Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddDbContext<LibraryDbContext>(o => o.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();
        var sut = new LlmIdentificationService(
            provider.GetRequiredService<IServiceScopeFactory>(), new BackgroundTaskCoordinator(),
            NullLogger<LlmIdentificationService>.Instance);

        var result = await sut.RunForTestsAsync(CancellationToken.None);

        Assert.False(result.Enabled);
        Assert.Equal(0, result.Calls);
    }
}

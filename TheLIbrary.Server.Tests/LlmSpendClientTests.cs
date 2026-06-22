using System.Text.Json;
using TheLibrary.Server.Services.Llm;
using Xunit;

namespace TheLibrary.Server.Tests;

public sealed class LlmSpendClientTests
{
    [Fact]
    public void Sums_OpenAi_Cost_Buckets()
    {
        // OpenAI shape: amount is an object { value, currency }.
        using var doc = JsonDocument.Parse(
            """
            {"object":"page","data":[
              {"results":[{"amount":{"value":0.06,"currency":"usd"}}]},
              {"results":[{"amount":{"value":1.50,"currency":"usd"}},{"amount":{"value":0.44,"currency":"usd"}}]}
            ]}
            """);
        Assert.Equal(2.00m, LlmSpendClient.SumBuckets(doc.RootElement));
    }

    [Fact]
    public void Sums_Anthropic_Cost_Buckets_With_String_Amounts()
    {
        // Anthropic shape: amount may be a bare string.
        using var doc = JsonDocument.Parse(
            """
            {"data":[
              {"results":[{"amount":"1.23","currency":"USD"}]},
              {"results":[{"amount":"0.77","currency":"USD"}]}
            ],"has_more":false}
            """);
        Assert.Equal(2.00m, LlmSpendClient.SumBuckets(doc.RootElement));
    }

    [Fact]
    public void Empty_Or_Shapeless_Payload_Is_Zero()
    {
        using var doc = JsonDocument.Parse("""{"object":"page","data":[]}""");
        Assert.Equal(0m, LlmSpendClient.SumBuckets(doc.RootElement));
    }
}

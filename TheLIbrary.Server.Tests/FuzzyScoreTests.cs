using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class FuzzyScoreTests
{
    [Fact]
    public void Identical_Returns_One()
    {
        Assert.Equal(1.0, FuzzyScore.JaroWinkler("magic kingdom", "magic kingdom"));
    }

    [Fact]
    public void Empty_Either_Side_Returns_Zero()
    {
        Assert.Equal(0.0, FuzzyScore.JaroWinkler("", "magic"));
        Assert.Equal(0.0, FuzzyScore.JaroWinkler("magic", ""));
        Assert.Equal(0.0, FuzzyScore.JaroWinkler(null, "magic"));
    }

    [Theory]
    [InlineData("echoes", "echos")]       // single typo, shared prefix
    [InlineData("the colour of magic", "the color of magic")] // UK/US variant
    [InlineData("foundation", "foundatoin")]                  // transposed adjacent
    public void Typos_Score_Above_0_85(string a, string b)
    {
        Assert.True(FuzzyScore.JaroWinkler(a, b) >= 0.85,
            $"Expected ≥0.85, got {FuzzyScore.JaroWinkler(a, b):F3} for '{a}' vs '{b}'");
    }

    [Fact]
    public void Different_Words_Score_Below_0_7()
    {
        Assert.True(FuzzyScore.JaroWinkler("magic kingdom", "second foundation") < 0.7);
    }

    [Fact]
    public void Score_Bounded_In_Zero_One()
    {
        // Sanity check: every pairing produces a value in the closed unit interval.
        foreach (var (a, b) in new[]
        {
            ("foundation", "foundation"),
            ("foundation", "second"),
            ("the magic kingdom", "the magic kingdoms"),
            ("a", "z"),
        })
        {
            var s = FuzzyScore.JaroWinkler(a, b);
            Assert.InRange(s, 0.0, 1.0);
        }
    }
}

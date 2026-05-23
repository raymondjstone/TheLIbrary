using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class FuzzyScoreMoreTests
{
    [Fact]
    public void JaroWinkler_Is_Symmetric()
    {
        var a = FuzzyScore.JaroWinkler("foundation", "foundatoin");
        var b = FuzzyScore.JaroWinkler("foundatoin", "foundation");

        Assert.Equal(a, b, 12);
    }

    [Fact]
    public void JaroWinkler_Single_Character_Match_Is_One()
    {
        Assert.Equal(1.0, FuzzyScore.JaroWinkler("a", "a"));
    }

    [Fact]
    public void JaroWinkler_Completely_Different_Single_Characters_Is_Zero()
    {
        Assert.Equal(0.0, FuzzyScore.JaroWinkler("a", "b"));
    }

    [Fact]
    public void Shared_Prefix_Gets_Higher_Score_Than_Same_Core_With_Different_Prefix()
    {
        var sharedPrefix = FuzzyScore.JaroWinkler("foundation and empire", "foundation and empier");
        var differentPrefix = FuzzyScore.JaroWinkler("second foundation", "third foundation");

        Assert.True(sharedPrefix > differentPrefix);
    }

    [Fact]
    public void Longer_Exact_Prefix_With_Typo_Beats_Shorter_Prefix_Typo()
    {
        var longPrefix = FuzzyScore.JaroWinkler("the colour of magic", "the color of magic");
        var shortPrefix = FuzzyScore.JaroWinkler("colour of magic", "xolour of magic");

        Assert.True(longPrefix > shortPrefix);
    }
}

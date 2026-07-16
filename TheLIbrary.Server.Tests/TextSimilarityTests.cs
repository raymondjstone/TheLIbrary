using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public sealed class TextSimilarityTests
{
    // TextSimilarity tokenizes on \p{L}+ (letters only), so a numeric suffix like
    // "word0" would strip to just "word" — every generated token must be pure
    // letters to actually produce `count` distinct words.
    private static string Words(int count, string prefix = "word")
        => string.Join(' ', Enumerable.Range(0, count).Select(i => $"{prefix}{ToAlpha(i)}"));

    private static string ToAlpha(int n)
    {
        var sb = new System.Text.StringBuilder();
        do { sb.Insert(0, (char)('a' + n % 26)); n = n / 26 - 1; } while (n >= 0);
        return sb.ToString();
    }

    [Fact]
    public void Identical_Text_Scores_100()
    {
        var text = Words(30);
        Assert.Equal(100, TextSimilarity.PercentSimilar(text, text));
    }

    [Fact]
    public void Completely_Different_Text_Scores_Zero()
    {
        var a = Words(30, "alpha");
        var b = Words(30, "beta");
        Assert.Equal(0, TextSimilarity.PercentSimilar(a, b));
    }

    [Fact]
    public void Mostly_Overlapping_Text_Scores_High_But_Not_100()
    {
        var shared = Words(28);
        var a = shared + " onlyA1 onlyA2";
        var b = shared + " onlyB1 onlyB2";
        var pct = TextSimilarity.PercentSimilar(a, b);
        Assert.NotNull(pct);
        Assert.InRange(pct!.Value, 80, 99.9);
    }

    [Fact]
    public void Too_Few_Words_Returns_Null_Not_A_Score()
    {
        var short1 = Words(5);
        var short2 = Words(5);
        Assert.Null(TextSimilarity.PercentSimilar(short1, short2, minWords: 20));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_Or_Missing_Text_Returns_Null(string? blank)
    {
        Assert.Null(TextSimilarity.PercentSimilar(blank, Words(30)));
        Assert.Null(TextSimilarity.PercentSimilar(Words(30), blank));
    }

    [Fact]
    public void Comparison_Is_Case_And_Line_Wrap_Insensitive()
    {
        var a = "The Quick Brown Fox\nJumped over the lazy dog while thinking about " + Words(20);
        var b = "the quick brown fox jumped over the lazy dog while thinking about " + Words(20);
        Assert.Equal(100, TextSimilarity.PercentSimilar(a, b));
    }
}

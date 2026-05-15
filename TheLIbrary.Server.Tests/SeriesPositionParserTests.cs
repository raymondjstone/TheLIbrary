using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class SeriesPositionParserTests
{
    private static string? Parse(string title) =>
        AuthorRefresher.ParseSeriesPosition(title);

    // ── Comma-separated variants ─────────────────────────────────────────────

    [Theory]
    [InlineData("Pawn of Prophecy (The Belgariad, #1)", "1")]
    [InlineData("Title (Series, Book 3)", "3")]
    [InlineData("Title (Series, Book #3)", "3")]
    [InlineData("Title (Series, 3)", "3")]
    [InlineData("Title (Series, Part 2)", "2")]
    [InlineData("Title (Series, Vol. 4)", "4")]
    [InlineData("Title (Series, Volume 1)", "1")]
    [InlineData("Title (Series, 1.5)", "1.5")]          // fractional / novella
    [InlineData("Title (Series, #1.5)", "1.5")]
    public void CommaVariants(string title, string expected) =>
        Assert.Equal(expected, Parse(title));

    // ── Space-separated variants (no comma) ──────────────────────────────────

    [Theory]
    [InlineData("Title (Series Book 3)", "3")]
    [InlineData("Title (Series #3)", "3")]
    [InlineData("Demon Lord of Karanda (The Malloreon, Book 3)", "3")]
    public void SpaceVariants(string title, string expected) =>
        Assert.Equal(expected, Parse(title));

    // ── Single-keyword parentheticals (series name IS the keyword) ───────────

    [Theory]
    [InlineData("Title (Book 3)", "3")]
    [InlineData("Title (Part 2)", "2")]
    [InlineData("Title (Vol. 4)", "4")]
    [InlineData("Title (Volume 1)", "1")]
    public void SingleKeywordVariants(string title, string expected) =>
        Assert.Equal(expected, Parse(title));

    // ── No match expected ────────────────────────────────────────────────────

    [Theory]
    [InlineData("Just a Title")]                        // no parenthetical
    [InlineData("Title (A Subtitle)")]                  // text only, no number
    [InlineData("Title (revised edition)")]             // no leading number token
    [InlineData("Title")]                               // bare title
    public void NoMatch(string title) =>
        Assert.Null(Parse(title));

    // ── Position appears anywhere in the title ───────────────────────────────

    [Fact]
    public void PositionAtEndOfLongTitle()
    {
        var title = "The Ruby Knight (The Elenium, Book 2)";
        Assert.Equal("2", Parse(title));
    }

    [Fact]
    public void DoubleDigitPosition()
    {
        Assert.Equal("12", Parse("Title (Series, Book 12)"));
    }
}

public class SeriesInfoFromTitleTests
{
    private static (string? Name, string? Position) Parse(string title) =>
        AuthorRefresher.ParseSeriesInfoFromTitle(title);

    // ── Comma-separated — name and position both extracted ───────────────────

    [Theory]
    [InlineData("Demon Lord of Karanda (The Malloreon, Book 3)", "The Malloreon", "3")]
    [InlineData("Pawn of Prophecy (The Belgariad, #1)",          "The Belgariad", "1")]
    [InlineData("Title (My Series, 2)",                          "My Series",     "2")]
    [InlineData("Title (My Series, Part 4)",                     "My Series",     "4")]
    [InlineData("Title (My Series, Vol. 5)",                     "My Series",     "5")]
    [InlineData("Title (My Series, Volume 1)",                   "My Series",     "1")]
    [InlineData("Title (My Series, #1.5)",                       "My Series",     "1.5")]
    public void CommaVariants(string title, string expectedName, string expectedPos)
    {
        var (name, pos) = Parse(title);
        Assert.Equal(expectedName, name);
        Assert.Equal(expectedPos, pos);
    }

    // ── Space-separated with explicit keyword ────────────────────────────────

    [Theory]
    [InlineData("Title (My Series Book 3)", "My Series", "3")]
    [InlineData("Title (My Series Part 2)", "My Series", "2")]
    public void SpaceWithKeywordVariants(string title, string expectedName, string expectedPos)
    {
        var (name, pos) = Parse(title);
        Assert.Equal(expectedName, name);
        Assert.Equal(expectedPos, pos);
    }

    // ── No match — title has no parseable series parenthetical ───────────────

    [Theory]
    [InlineData("Just a Title")]
    [InlineData("Title (A Subtitle)")]
    [InlineData("Title (revised edition)")]
    [InlineData("Title")]
    public void NoMatch(string title)
    {
        var (name, pos) = Parse(title);
        Assert.Null(name);
        Assert.Null(pos);
    }
}

using TheLibrary.Server.Services.Search;
using Xunit;

namespace TheLibrary.Server.Tests;

public sealed class FullTextTokenizeTests
{
    [Fact]
    public void Folds_Ligatures_So_Collation_Equal_Tokens_Dedupe()
    {
        // "ﬁrst" (U+FB01 ligature) and "first" are distinct to ordinal C# but equal
        // under SQL Server's CI collation — they used to collide on the word PK.
        // After NFKC folding they must collapse to a single "first".
        var words = FullTextSearchService.Tokenize("ﬁrst first FIRST", 1000);
        Assert.Single(words);
        Assert.Equal("first", words[0]);
    }

    [Fact]
    public void Lowercases_And_Dedupes_Plain_Tokens()
    {
        var words = FullTextSearchService.Tokenize("Hello hello HELLO world", 1000);
        Assert.Equal(new[] { "hello", "world" }, words);
    }

    [Fact]
    public void Respects_The_Cap()
    {
        var words = FullTextSearchService.Tokenize("alpha bravo charlie delta echo", 3);
        Assert.Equal(3, words.Count);
    }
}

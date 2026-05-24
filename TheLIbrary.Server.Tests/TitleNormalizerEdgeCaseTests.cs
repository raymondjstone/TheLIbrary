using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class TitleNormalizerEdgeCaseTests
{
    [Theory]
    [InlineData("The   Final Empire", "final empire")]
    [InlineData("A Tale!!! Of??? Two---Cities", "tale of two cities")]
    [InlineData("An    Ember   in the   Ashes", "ember in the ashes")]
    [InlineData("Title (123)", "title")]
    [InlineData("L'étranger", "l etranger")]
    public void Normalize_Handles_Whitespace_Punctuation_And_Trailing_Id(string input, string expected)
    {
        Assert.Equal(expected, TitleNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_Truncates_To_MaxNormalizedLength()
    {
        var input = "The " + new string('a', TitleNormalizer.MaxNormalizedLength + 50);
        var normalized = TitleNormalizer.Normalize(input);

        Assert.Equal(TitleNormalizer.MaxNormalizedLength, normalized.Length);
        Assert.DoesNotContain("  ", normalized);
    }

    [Theory]
    [InlineData("The Book by Rowan Vale", new[] { "book by rowan vale", "book" })]
    [InlineData("The Book (Rowan Vale) by Someone Else", new[] { "book rowan vale by someone else", "book rowan vale" })]
    [InlineData("Standalone", new[] { "standalone" })]
    public void FolderTitleCandidates_Returns_Expected_Candidate_Order(string folder, string[] expected)
    {
        Assert.Equal(expected, TitleNormalizer.FolderTitleCandidates(folder).ToArray());
    }

    [Fact]
    public void FolderTitleCandidates_Deduplicates_Equivalent_Transforms()
    {
        var candidates = TitleNormalizer.FolderTitleCandidates("Foundation (Isaac Asimov) by Isaac Asimov").ToList();

        Assert.Equal(candidates.Count, candidates.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("Arthur C Clarke", true)]
    [InlineData("Mary Shelley", true)]
    [InlineData("[美] Author", false)]
    [InlineData("2Fast 2Furious", false)]
    [InlineData("for my mother", false)]
    [InlineData("LU", false)]
    [InlineData("'Nathan", false)]
    [InlineData("A.B.", false)]
    public void IsPlausibleAuthorName_Handles_Extra_Shapes(string input, bool expected)
    {
        Assert.Equal(expected, TitleNormalizer.IsPlausibleAuthorName(input));
    }

    [Theory]
    [InlineData("[Series 02] - Title - Author", "Series", "2", "Title", "Author")]
    [InlineData("(Series 02) Title - Author", "Series", "2", "Title", "Author")]
    [InlineData("Author - {Series 04} - Title", "Series", "4", "Title", "Author")]
    [InlineData("Series, Volume 01 - Title - Author", "Series", "1", "Title", "Author")]
    [InlineData("Series, Part 07 - Title", "Series", "7", "Title", null)]
    [InlineData("Series - #09 - Title - Author", "Series", "9", "Title", "Author")]
    public void TryParseSeriesFilename_Handles_Additional_Valid_Forms(string stem, string? series, string? pos, string? title, string? author)
    {
        var actual = TitleNormalizer.TryParseSeriesFilename(stem);
        Assert.Equal((series, pos, title, author), actual);
    }

    [Theory]
    [InlineData("{} - 02 - Title")]
    [InlineData("[] - 03 - Title")]
    [InlineData("Title - Author - Extra")]
    [InlineData("123 - 456 - 789")]
    public void TryParseSeriesFilename_Rejects_Invalid_Or_Ambiguous_Forms(string stem)
    {
        var actual = TitleNormalizer.TryParseSeriesFilename(stem);
        Assert.Equal((null, null, null, null), actual);
    }

    [Theory]
    [InlineData("Asimov, Isaac", "isaac asimov")]
    [InlineData("  Ursula   K. Le Guin ", "ursula k le guin")]
    [InlineData("René Descartes", "rene descartes")]
    [InlineData(null, "")]
    public void NormalizeAuthor_Handles_More_Inputs(string? input, string expected)
    {
        Assert.Equal(expected, TitleNormalizer.NormalizeAuthor(input));
    }
}

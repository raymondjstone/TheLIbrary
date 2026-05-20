using TheLibrary.Server.Services.Calibre;
using Xunit;

namespace TheLibrary.Server.Tests;

public class IsbnNormaliseTests
{
    [Theory]
    [InlineData("9780743247221", "9780743247221")]          // bare ISBN-13
    [InlineData("978-0-7432-4722-1", "9780743247221")]      // hyphenated
    [InlineData("978 0 7432 4722 1", "9780743247221")]      // spaced
    [InlineData("urn:isbn:9780743247221", "9780743247221")] // URN form
    [InlineData("ISBN:978-0-7432-4722-1", "9780743247221")] // explicit prefix
    [InlineData("0743247221", "0743247221")]                // bare ISBN-10
    [InlineData("0-7432-4722-1", "0743247221")]             // hyphenated ISBN-10
    [InlineData("080442957X", "080442957X")]                // ISBN-10 with X check digit
    [InlineData("080442957x", "080442957X")]                // lowercase x normalised
    public void Recognises_Valid_Isbns(string input, string expected)
    {
        Assert.Equal(expected, EpubMetadataReader.NormaliseIsbn(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-isbn")]
    [InlineData("12345")]            // too short
    [InlineData("12345678901")]      // 11 digits
    [InlineData("12345678901234")]   // 14 digits
    public void Rejects_Invalid_Inputs(string? input)
    {
        Assert.Null(EpubMetadataReader.NormaliseIsbn(input ?? ""));
    }
}

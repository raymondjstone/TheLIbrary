using TheLibrary.Server.Data.Models;
using Xunit;

namespace TheLibrary.Server.Tests;

public class IsbnKeyTests
{
    [Theory]
    // Valid ISBN-13s (real check digits) — normalized to bare digits.
    [InlineData("9780307762726", "9780307762726")]
    [InlineData("978-0-306-40615-7", "9780306406157")]
    // Valid ISBN-10s, including a trailing X and a lowercased x.
    [InlineData("0306406152", "0306406152")]
    [InlineData("080442957X", "080442957X")]
    [InlineData("080442957x", "080442957X")]
    public void IsbnKey_Accepts_Valid_Isbns(string raw, string expected)
        => Assert.Equal(expected, IsbnResolution.IsbnKey(raw));

    [Theory]
    // Right length, WRONG check digit — the mis-extracted numbers (LCCN, ASIN,
    // copyright-page junk) that were polluting the cache with null-work rows.
    [InlineData("1998841087")]   // 10 digits, bad checksum
    [InlineData("2462991856")]   // 10 digits, bad checksum
    [InlineData("9780307762727")] // 13 digits, last digit off by one
    // Wrong length / empty.
    [InlineData("12345")]
    [InlineData("")]
    [InlineData(null)]
    public void IsbnKey_Rejects_Invalid_Isbns(string? raw)
        => Assert.Null(IsbnResolution.IsbnKey(raw));
}

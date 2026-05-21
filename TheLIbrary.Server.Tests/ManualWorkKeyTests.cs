using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class ManualWorkKeyTests
{
    [Fact]
    public void NewCandidate_MatchesXxEightDigitWShape()
    {
        for (var i = 0; i < 500; i++)
        {
            var key = ManualWorkKey.NewCandidate();
            Assert.Matches(@"^XX\d{8}W$", key);
            Assert.True(ManualWorkKey.IsManual(key));
        }
    }

    [Theory]
    [InlineData("XX00000001W", true)]
    [InlineData("XX12345678W", true)]
    [InlineData("OL12345678W", false)]
    [InlineData("OL999W", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsManual_OnlyTrueForXxKeys(string? key, bool expected)
        => Assert.Equal(expected, ManualWorkKey.IsManual(key));
}

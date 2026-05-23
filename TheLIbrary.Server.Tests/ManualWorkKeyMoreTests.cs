using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class ManualWorkKeyMoreTests
{
    [Fact]
    public void Prefix_Constant_Is_XX()
    {
        Assert.Equal("XX", ManualWorkKey.Prefix);
    }

    [Theory]
    [InlineData("XX")]
    [InlineData("XX1")]
    [InlineData("XXabcdefghW")]
    public void IsManual_Returns_True_For_Any_Ordinal_XX_Prefix(string key)
    {
        Assert.True(ManualWorkKey.IsManual(key));
    }

    [Theory]
    [InlineData("xx12345678W")]
    [InlineData("Ax12345678W")]
    [InlineData(" OL123W")]
    public void IsManual_Is_CaseSensitive_And_Does_Not_Trim(string key)
    {
        Assert.False(ManualWorkKey.IsManual(key));
    }

    [Fact]
    public void NewCandidate_Always_Starts_With_Prefix_And_Ends_With_W()
    {
        for (var i = 0; i < 100; i++)
        {
            var key = ManualWorkKey.NewCandidate();
            Assert.StartsWith(ManualWorkKey.Prefix, key, StringComparison.Ordinal);
            Assert.EndsWith("W", key, StringComparison.Ordinal);
        }
    }
}

using TheLibrary.Server.Services.Incoming;
using Xunit;

namespace TheLibrary.Server.Tests;

public class UnknownFolderRecoveryMoreTests
{
    private static AuthorIndexEntry Tracked(string name, int id, string? folder = null, IReadOnlyList<string>? aliases = null) =>
        new(name, folder ?? name, true, id, AlternateNames: aliases);

    [Fact]
    public void Plan_Preserves_Input_Order_Across_Matched_And_Unmatched()
    {
        var matcher = new AuthorMatcher(new[] { Tracked("Lena Hart", 1) });

        var plan = UnknownFolderRecovery.Plan(new[] { "Unknown", "Lena Hart", "Another Unknown" }, matcher);

        Assert.Equal(new[] { "Lena Hart" }, plan.Matched.Select(x => x.FolderName).ToArray());
        Assert.Equal(new[] { "Unknown", "Another Unknown" }, plan.Unmatched.ToArray());
    }

    [Fact]
    public void Plan_Matches_Tracked_Alias_But_Not_Ol_Alias()
    {
        var matcher = new AuthorMatcher(new[]
        {
            Tracked("Lena Hart", 1, aliases: new[] { "L. Hart" }),
            new AuthorIndexEntry("Ari Mercer", "Ari Mercer", false, AlternateNames: new[] { "A. Mercer" })
        });

        var plan = UnknownFolderRecovery.Plan(new[] { "L. Hart", "A. Mercer" }, matcher);

        Assert.Single(plan.Matched);
        Assert.Single(plan.Unmatched);
        Assert.Equal("A. Mercer", plan.Unmatched[0]);
    }

    [Fact]
    public void Plan_Treats_Case_Variants_As_Matches()
    {
        var matcher = new AuthorMatcher(new[] { Tracked("Lena Hart", 1) });

        var plan = UnknownFolderRecovery.Plan(new[] { "lena hart", "LENA HART" }, matcher);

        Assert.Equal(2, plan.Matched.Count);
    }

    [Fact]
    public void Plan_Handles_Empty_Input()
    {
        var matcher = new AuthorMatcher(new[] { Tracked("Lena Hart", 1) });

        var plan = UnknownFolderRecovery.Plan(Array.Empty<string>(), matcher);

        Assert.Empty(plan.Matched);
        Assert.Empty(plan.Unmatched);
    }
}

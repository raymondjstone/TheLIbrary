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
        var matcher = new AuthorMatcher(new[] { Tracked("Terry Brooks", 1) });

        var plan = UnknownFolderRecovery.Plan(new[] { "Unknown", "Terry Brooks", "Another Unknown" }, matcher);

        Assert.Equal(new[] { "Terry Brooks" }, plan.Matched.Select(x => x.FolderName).ToArray());
        Assert.Equal(new[] { "Unknown", "Another Unknown" }, plan.Unmatched.ToArray());
    }

    [Fact]
    public void Plan_Matches_Tracked_Alias_But_Not_Ol_Alias()
    {
        var matcher = new AuthorMatcher(new[]
        {
            Tracked("Terry Brooks", 1, aliases: new[] { "T. Brooks" }),
            new AuthorIndexEntry("Isaac Asimov", "Isaac Asimov", false, AlternateNames: new[] { "I. Asimov" })
        });

        var plan = UnknownFolderRecovery.Plan(new[] { "T. Brooks", "I. Asimov" }, matcher);

        Assert.Single(plan.Matched);
        Assert.Single(plan.Unmatched);
        Assert.Equal("I. Asimov", plan.Unmatched[0]);
    }

    [Fact]
    public void Plan_Treats_Case_Variants_As_Matches()
    {
        var matcher = new AuthorMatcher(new[] { Tracked("Terry Brooks", 1) });

        var plan = UnknownFolderRecovery.Plan(new[] { "terry brooks", "TERRY BROOKS" }, matcher);

        Assert.Equal(2, plan.Matched.Count);
    }

    [Fact]
    public void Plan_Handles_Empty_Input()
    {
        var matcher = new AuthorMatcher(new[] { Tracked("Terry Brooks", 1) });

        var plan = UnknownFolderRecovery.Plan(Array.Empty<string>(), matcher);

        Assert.Empty(plan.Matched);
        Assert.Empty(plan.Unmatched);
    }
}

using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class SameNameAuthorServiceTests
{
    [Fact]
    public void SummarizeForTests_Adds_Unique_New_Keys_And_Warnings_For_Empty_Catalog()
    {
        var summary = SameNameAuthorService.SummarizeForTests([], new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.Ordinal), 3);

        Assert.Equal(0, summary.AuthorsAdded);
        Assert.Single(summary.Warnings);
    }

    [Fact]
    public void SummarizeForTests_Skips_Blacklisted_Names()
    {
        var summary = SameNameAuthorService.SummarizeForTests(
            [("OL1A", "Shared Name", "shared name")],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.Ordinal) { "shared name" },
            1);

        Assert.Equal(0, summary.AuthorsAdded);
    }

    [Fact]
    public void SummarizeForTests_Skips_Tracked_And_Deduplicates_New_Keys()
    {
        var summary = SameNameAuthorService.SummarizeForTests(
            [("OL1A", "Shared Name", "shared name"), ("OL2A", "Shared Name", "shared name"), ("OL2A", "Shared Name", "shared name")],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "OL1A" },
            new HashSet<string>(StringComparer.Ordinal),
            1);

        Assert.Equal(1, summary.AuthorsAdded);
        Assert.Single(summary.Added);
    }
}

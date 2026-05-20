using TheLibrary.Server.Services.Incoming;
using Xunit;

namespace TheLibrary.Server.Tests;

// Covers the pure-function planning step that decides which __unknown folders
// can be rematched against the tracked-author watchlist. Disk I/O happens
// outside this module so the assertions stay deterministic.
public class UnknownFolderRecoveryTests
{
    private static AuthorIndexEntry Tracked(string name, string? folder = null,
        IReadOnlyList<string>? alternates = null, int? id = null) =>
        new(name, folder ?? name, IsTracked: true, TrackedAuthorId: id, AlternateNames: alternates);

    private static AuthorMatcher BuildMatcher(params AuthorIndexEntry[] entries) =>
        new(entries);

    // ── Plain matches by display name / folder name ──────────────────────────

    [Fact]
    public void Matches_folder_with_same_normalized_name()
    {
        var matcher = BuildMatcher(Tracked("Terry Brooks", id: 1));
        var plan = UnknownFolderRecovery.Plan(new[] { "Terry Brooks" }, matcher);

        Assert.Single(plan.Matched);
        Assert.Empty(plan.Unmatched);
        Assert.Equal("Terry Brooks", plan.Matched[0].Match?.DisplayName);
    }

    [Fact]
    public void Matches_calibre_folder_form_last_comma_first()
    {
        // NormalizeAuthor flips comma form, so "Brooks, Terry" → "terry brooks"
        var matcher = BuildMatcher(Tracked("Terry Brooks", id: 1));
        var plan = UnknownFolderRecovery.Plan(new[] { "Brooks, Terry" }, matcher);

        Assert.Single(plan.Matched);
        Assert.Equal("Brooks, Terry", plan.Matched[0].FolderName);
    }

    [Fact]
    public void Matches_via_surname_first_rotation()
    {
        // ExpandNameVariants emits "brooks terry" alongside "terry brooks"
        var matcher = BuildMatcher(Tracked("Terry Brooks", id: 1));
        var plan = UnknownFolderRecovery.Plan(new[] { "Brooks Terry" }, matcher);

        Assert.Single(plan.Matched);
    }

    // ── AlternateNames bring in non-obvious matches ─────────────────────────

    [Fact]
    public void Matches_alternate_name_from_index_entry()
    {
        var matcher = BuildMatcher(Tracked(
            "Terry Brooks", id: 1,
            alternates: new[] { "T. Brooks", "Terence Brooks" }));

        var plan = UnknownFolderRecovery.Plan(
            new[] { "T. Brooks", "Terence Brooks", "Terry Brooks" }, matcher);

        Assert.Equal(3, plan.Matched.Count);
        Assert.Empty(plan.Unmatched);
        Assert.All(plan.Matched, m => Assert.Equal(1, m.Match?.TrackedAuthorId));
    }

    [Fact]
    public void Diacritic_variation_in_alternate_still_matches()
    {
        var matcher = BuildMatcher(Tracked(
            "Stanislaw Lem", id: 1,
            alternates: new[] { "Stanisław Lem" }));   // Polish ł

        var plan = UnknownFolderRecovery.Plan(new[] { "Stanislaw Lem" }, matcher);
        Assert.Single(plan.Matched);
    }

    // ── Non-matches stay in the unmatched bucket ─────────────────────────────

    [Fact]
    public void Unknown_author_lands_in_unmatched()
    {
        var matcher = BuildMatcher(Tracked("Terry Brooks", id: 1));
        var plan = UnknownFolderRecovery.Plan(
            new[] { "Asimov, Isaac", "Unknown Author" }, matcher);

        Assert.Empty(plan.Matched);
        Assert.Equal(2, plan.Unmatched.Count);
    }

    [Fact]
    public void Whitespace_and_empty_folder_names_are_skipped_entirely()
    {
        var matcher = BuildMatcher(Tracked("Terry Brooks", id: 1));
        var plan = UnknownFolderRecovery.Plan(new[] { "", "   ", "Terry Brooks" }, matcher);

        // Empty/whitespace inputs aren't counted as unmatched either — they
        // are dropped silently because they could never be legitimate folder
        // names to begin with.
        Assert.Single(plan.Matched);
        Assert.Empty(plan.Unmatched);
    }

    // ── OL-only entries (IsTracked=false) do NOT count as a match ────────────

    [Fact]
    public void OL_only_entry_does_not_count_as_matched()
    {
        // Folder recovery is about routing back to a tracked author folder —
        // OL-only catalog hits are not tracked, so they should fall through.
        var matcher = new AuthorMatcher(new[]
        {
            new AuthorIndexEntry("Terry Brooks", "Terry Brooks", IsTracked: false)
        });
        var plan = UnknownFolderRecovery.Plan(new[] { "Terry Brooks" }, matcher);

        Assert.Empty(plan.Matched);
        Assert.Single(plan.Unmatched);
    }

    // ── First-tracked-hit wins on overlapping keys ───────────────────────────

    [Fact]
    public void Tracked_entry_wins_over_OL_entry_on_same_key()
    {
        var matcher = new AuthorMatcher(new[]
        {
            new AuthorIndexEntry("Terry Brooks", "Terry Brooks", IsTracked: false),       // OL stub
            Tracked("Terry Brooks", folder: "Brooks-Terry", id: 7),                       // tracked
        });
        var plan = UnknownFolderRecovery.Plan(new[] { "Terry Brooks" }, matcher);

        Assert.Single(plan.Matched);
        Assert.True(plan.Matched[0].Match?.IsTracked);
        Assert.Equal(7, plan.Matched[0].Match?.TrackedAuthorId);
    }

    // ── Blacklisted authors are skipped at index time ────────────────────────

    [Fact]
    public void Blacklisted_author_is_never_returned()
    {
        // The matcher normalises both display + folder names. Add the
        // normalized form of "Terry Brooks" to the blacklist; the folder
        // should now miss.
        var matcher = new AuthorMatcher(
            new[] { Tracked("Terry Brooks", id: 1) },
            new[] { "terry brooks" });

        var plan = UnknownFolderRecovery.Plan(new[] { "Terry Brooks" }, matcher);
        Assert.Empty(plan.Matched);
        Assert.Single(plan.Unmatched);
    }

    [Fact]
    public void Blacklisted_alias_does_not_taint_canonical_match()
    {
        // The blacklist works per author, not per alias — so if the canonical
        // name normalises to something the blacklist contains, the whole entry
        // is skipped. Aliases of a NON-blacklisted entry continue to match.
        var matcher = new AuthorMatcher(
            new[] { Tracked("Terry Brooks", id: 1, alternates: new[] { "T. Brooks" }) },
            new[] { "totally different" });

        var plan = UnknownFolderRecovery.Plan(new[] { "T. Brooks", "Terry Brooks" }, matcher);
        Assert.Equal(2, plan.Matched.Count);
    }

    // ── Duplicates in input are reported per-folder, no merging ──────────────

    [Fact]
    public void Duplicate_folder_names_each_produce_a_decision()
    {
        var matcher = BuildMatcher(Tracked("Terry Brooks", id: 1));
        var plan = UnknownFolderRecovery.Plan(
            new[] { "Terry Brooks", "Terry Brooks" }, matcher);

        Assert.Equal(2, plan.Matched.Count);
        Assert.Empty(plan.Unmatched);
    }
}

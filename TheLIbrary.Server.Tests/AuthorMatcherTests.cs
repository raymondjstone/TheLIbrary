using TheLibrary.Server.Services.Incoming;
using Xunit;

namespace TheLibrary.Server.Tests;

// Covers the author-matching algorithm shared by incoming / reprocess-unknown.
// AuthorMatcher treats tracked watchlist entries and OpenLibraryAuthor catalog
// rows the same way — only the IsTracked flag differs. The index keys come
// from TitleNormalizer.NormalizeAuthor plus surname/forename rotations, so
// tests here assert each of those paths end-to-end using in-memory data.
public class AuthorMatcherTests
{
    private static AuthorIndexEntry Tracked(string name, string? folder = null) =>
        new(name, folder ?? name, IsTracked: true);

    private static AuthorIndexEntry Ol(string name) =>
        new(name, name, IsTracked: false);

    // Mixed index: one watchlist author + a few OL-catalog-only authors. The
    // same instance backs every assertion below so "matches tracked" and
    // "matches OL" are exercised through the same code path.
    private static AuthorMatcher BuildMatcher() => new(new[]
    {
        Tracked("Mira C. Rowan"),
        Tracked("Rowan Vale", folder: "Vale, Rowan"),
        Ol("Ari Mercer"),
        Ol("Elin Ward"),
        Ol("Júlia Soria"),
    });

    // ---------- forward "Author - Title.ext" filename matching ---------------

    [Fact]
    public void Matches_tracked_author_from_forward_filename()
    {
        var m = BuildMatcher();
        var r = m.Resolve(null, null, @"X:\drop\Mira C. Rowan - Signal over Haven.epub");

        Assert.NotNull(r);
        Assert.True(r!.Entry.IsTracked);
        Assert.Equal("Mira C. Rowan", r.Entry.FolderName);
        Assert.Null(r.RewrittenTitle);
    }

    [Fact]
    public void Matches_OpenLibrary_author_from_forward_filename()
    {
        var m = BuildMatcher();
        var r = m.Resolve(null, null, @"X:\drop\Ari Mercer - Cornerstone.epub");

        Assert.NotNull(r);
        Assert.False(r!.Entry.IsTracked);
        Assert.Equal("Ari Mercer", r.Entry.FolderName);
    }

    // ---------- reverse "Title - Author.ext" filename matching ---------------

    [Fact]
    public void Matches_tracked_author_from_reverse_filename()
    {
        var m = BuildMatcher();
        var r = m.Resolve(null, null, @"X:\drop\Signal over Haven - Mira C. Rowan.epub");

        Assert.NotNull(r);
        Assert.True(r!.Entry.IsTracked);
        Assert.Equal("Signal over Haven", r.RewrittenTitle);
    }

    [Fact]
    public void Matches_OpenLibrary_author_from_reverse_filename()
    {
        var m = BuildMatcher();
        var r = m.Resolve(null, null, @"X:\drop\Cornerstone - Ari Mercer.mobi");

        Assert.NotNull(r);
        Assert.False(r!.Entry.IsTracked);
        Assert.Equal("Ari Mercer", r.Entry.FolderName);
        Assert.Equal("Cornerstone", r.RewrittenTitle);
    }

    [Fact]
    public void Reverse_filename_splits_on_last_dash_so_titles_with_dashes_work()
    {
        var m = BuildMatcher();
        // "Book - Part 2 - Author" — the author lives AFTER the last " - ".
        var r = m.Resolve(null, null, @"X:\drop\Book - Part 2 - Mira C. Rowan.epub");

        Assert.NotNull(r);
        Assert.Equal("Book - Part 2", r!.RewrittenTitle);
    }

    // ---------- metadata-author matching (dc:creator etc) --------------------

    [Fact]
    public void Matches_tracked_author_from_metadata()
    {
        var m = BuildMatcher();
        var r = m.Resolve("Mira C. Rowan", null, @"X:\drop\some-opaque-filename.epub");

        Assert.NotNull(r);
        Assert.True(r!.Entry.IsTracked);
    }

    [Fact]
    public void Matches_OpenLibrary_author_from_metadata()
    {
        var m = BuildMatcher();
        var r = m.Resolve("Elin Ward", null, @"X:\drop\some-opaque-filename.epub");

        Assert.NotNull(r);
        Assert.False(r!.Entry.IsTracked);
        Assert.Equal("Elin Ward", r.Entry.FolderName);
    }

    [Fact]
    public void Comma_form_metadata_matches_space_form_entry()
    {
        // "Last, First" style — NormalizeAuthor flips these before lookup.
        var m = BuildMatcher();
        var r = m.Resolve("Rowan, Mira C.", null, @"X:\drop\book.epub");

        Assert.NotNull(r);
        Assert.Equal("Mira C. Rowan", r!.Entry.DisplayName);
    }

    [Fact]
    public void Authorsort_metadata_is_probed_when_primary_author_does_not_match()
    {
        // Kindle files sometimes fill author as display form AND author_sort
        // as "Last, First" separately — both should land on the same entry.
        var m = BuildMatcher();
        var r = m.Resolve(null, "Rowan, Mira C.", @"X:\drop\book.epub");

        Assert.NotNull(r);
        Assert.Equal("Mira C. Rowan", r!.Entry.DisplayName);
    }

    [Fact]
    public void Diacritics_are_stripped_so_accented_names_still_match()
    {
        var m = BuildMatcher();
        var r1 = m.Resolve("Julia Soria", null, @"X:\drop\book.epub");
        var r2 = m.Resolve("Júlia Soria", null, @"X:\drop\book.epub");

        Assert.NotNull(r1);
        Assert.NotNull(r2);
        Assert.Equal(r1!.Entry.DisplayName, r2!.Entry.DisplayName);
    }

    // ---------- name-variant rotations ---------------------------------------

    [Fact]
    public void Space_form_surname_first_matches_forename_first_entry()
    {
        // Index has "Mira C. Rowan"; probe "Rowan Mira C" (Last + rest).
        var m = BuildMatcher();
        var r = m.Resolve("Rowan Mira C", null, @"X:\drop\book.epub");

        Assert.NotNull(r);
        Assert.Equal("Mira C. Rowan", r!.Entry.DisplayName);
    }

    [Fact]
    public void ExpandNameVariants_emits_original_and_both_rotations_for_three_tokens()
    {
        var variants = AuthorMatcher.ExpandNameVariants("mira c rowan").ToList();

        Assert.Contains("mira c rowan", variants);
        Assert.Contains("rowan mira c", variants);   // last + rest
        Assert.Contains("c rowan mira", variants);   // rest + first
    }

    [Fact]
    public void ExpandNameVariants_collapses_rotations_for_two_token_names()
    {
        var variants = AuthorMatcher.ExpandNameVariants("mira rowan").ToList();

        Assert.Contains("mira rowan", variants);
        Assert.Contains("rowan mira", variants);
        // The "rest + first" rotation collapses to the same string and isn't
        // re-emitted; two distinct variants is all we expect.
        Assert.Equal(2, variants.Count);
    }

    // Physical-books import / rematch uses ExpandNameVariants on both the input
    // author and the candidate Book's author so any rotation matches. These
    // pairings cover the common inventory shapes this matcher must tolerate.
    [Theory]
    [InlineData("Lena Hart", "Hart, Lena")]    // comma flip
    [InlineData("Lena Hart", "Hart Lena")]     // comma-less surname-first
    [InlineData("Hart, Lena", "Hart Lena")]    // mixed
    [InlineData("Mira C. Rowan", "Rowan, Mira C.")]
    [InlineData("Mira C. Rowan", "Rowan Mira C")]
    public void Variant_sets_overlap_for_real_inventory_pairs(string a, string b)
    {
        var ax = AuthorMatcher.ExpandNameVariants(
            TheLibrary.Server.Services.Sync.TitleNormalizer.NormalizeAuthor(a)).ToHashSet();
        var bx = AuthorMatcher.ExpandNameVariants(
            TheLibrary.Server.Services.Sync.TitleNormalizer.NormalizeAuthor(b)).ToHashSet();
        Assert.True(ax.Overlaps(bx),
            $"Expected variants of '{a}' ({string.Join("/", ax)}) and '{b}' ({string.Join("/", bx)}) to overlap");
    }

    [Theory]
    [InlineData("Lena Hart", "Ari Mercer")]      // genuinely different people
    [InlineData("Hart, Lena", "Hart, Serena")]   // same surname, different forename
    public void Variant_sets_do_NOT_overlap_for_different_authors(string a, string b)
    {
        var ax = AuthorMatcher.ExpandNameVariants(
            TheLibrary.Server.Services.Sync.TitleNormalizer.NormalizeAuthor(a)).ToHashSet();
        var bx = AuthorMatcher.ExpandNameVariants(
            TheLibrary.Server.Services.Sync.TitleNormalizer.NormalizeAuthor(b)).ToHashSet();
        Assert.False(ax.Overlaps(bx),
            $"Expected NO overlap between '{a}' ({string.Join("/", ax)}) and '{b}' ({string.Join("/", bx)})");
    }

    // ---------- tracked wins over OL on index collision ----------------------

    [Fact]
    public void Tracked_entry_wins_over_OL_entry_with_same_normalized_key()
    {
        var m = new AuthorMatcher(new[]
        {
            Ol("Mira C. Rowan"),                  // OL-catalog version
            Tracked("Mira C. Rowan", folder: "Rowan-Mira"),
        });
        var r = m.Resolve("Mira C. Rowan", null, @"X:\drop\book.epub");

        Assert.NotNull(r);
        Assert.True(r!.Entry.IsTracked);
        Assert.Equal("Rowan-Mira", r.Entry.FolderName);
    }

    // ---------- total miss ---------------------------------------------------

    [Fact]
    public void Returns_null_when_author_is_not_in_either_set()
    {
        var m = BuildMatcher();
        var r = m.Resolve("Nobody Nobody", null, @"X:\drop\Nobody Nobody - Thing.epub");
        Assert.Null(r);
    }

    [Fact]
    public void Returns_null_when_filename_has_no_dash_and_no_metadata()
    {
        var m = BuildMatcher();
        var r = m.Resolve(null, null, @"X:\drop\opaque-name.epub");
        Assert.Null(r);
    }

    // ---------- AlternateNames index expansion -------------------------------

    [Fact]
    public void Matches_via_alternate_name_entry()
    {
        var matcher = new AuthorMatcher(new[]
        {
            new AuthorIndexEntry(
                DisplayName: "Terry Brooks",
                FolderName: "Terry Brooks",
                IsTracked: true,
                TrackedAuthorId: 7,
                AlternateNames: new[] { "T. Brooks", "Terence Brooks" }),
        });

        // An alternate name probed through the index resolves to the same entry.
        Assert.NotNull(matcher.TryGet("T. Brooks"));
        Assert.Equal(7, matcher.TryGet("T. Brooks")!.TrackedAuthorId);

        // And via the comma-flipped variant.
        Assert.NotNull(matcher.TryGet("Brooks, Terence"));
    }

    [Fact]
    public void Alternate_name_does_not_promote_OL_over_tracked()
    {
        // OL entry whose alias collides with a tracked author's primary name —
        // the tracked entry must still win on probe.
        var matcher = new AuthorMatcher(new[]
        {
            new AuthorIndexEntry("Someone Else", "Someone Else", IsTracked: false,
                AlternateNames: new[] { "Terry Brooks" }),
            new AuthorIndexEntry("Terry Brooks", "Brooks-Terry", IsTracked: true, TrackedAuthorId: 9),
        });

        var hit = matcher.TryGet("Terry Brooks");
        Assert.NotNull(hit);
        Assert.True(hit!.IsTracked);
        Assert.Equal(9, hit.TrackedAuthorId);
    }

    [Fact]
    public void Empty_alternate_names_list_is_ignored_safely()
    {
        var matcher = new AuthorMatcher(new[]
        {
            new AuthorIndexEntry("Terry Brooks", "Terry Brooks", IsTracked: true,
                AlternateNames: Array.Empty<string>()),
        });
        Assert.NotNull(matcher.TryGet("Terry Brooks"));
    }

    // ---------- folder-layout ancestor walk ----------------------------------

    [Fact]
    public void Folder_layout_finds_tracked_author_from_ancestor_folder()
    {
        var m = BuildMatcher();
        var (entry, title) = m.ResolveFolderLayout(
            folderPath: @"X:\drop\Mira C. Rowan\Signal over Haven",
            sourceRoot: @"X:\drop");

        Assert.NotNull(entry);
        Assert.True(entry!.IsTracked);
        Assert.Equal("Signal over Haven", title);
    }

    [Fact]
    public void Folder_layout_finds_OL_author_from_ancestor_folder()
    {
        var m = BuildMatcher();
        var (entry, title) = m.ResolveFolderLayout(
            folderPath: @"X:\drop\Ari Mercer\Cornerstone",
            sourceRoot: @"X:\drop");

        Assert.NotNull(entry);
        Assert.False(entry!.IsTracked);
        Assert.Equal("Cornerstone", title);
    }

    [Fact]
    public void Folder_layout_skips_the_unknown_quarantine_folder()
    {
        var m = BuildMatcher();
        // "__unknown" should be ignored as an author name so a file under
        // __unknown/ doesn't self-match to a literal author named "__unknown".
        var (entry, _) = m.ResolveFolderLayout(
            folderPath: @"X:\drop\__unknown",
            sourceRoot: @"X:\drop");

        Assert.Null(entry);
    }

    // ---------- GetProbeKeys parity with Resolve -----------------------------

    [Fact]
    public void GetProbeKeys_includes_metadata_sort_and_reverse_filename_variants()
    {
        var m = BuildMatcher();
        var keys = m.GetProbeKeys(
            metadataAuthor: "Clarke, Arthur C.",
            metadataAuthorSort: null,
            filePath: @"X:\drop\Rama - Arthur C. Clarke.epub")
            .Select(k => k.Key)
            .ToList();

        // metadata-author probe (after NormalizeAuthor flips the comma form)
        Assert.Contains("arthur c clarke", keys);
        // reverse-filename probe (right side of last " - ")
        Assert.Contains("clarke arthur c", keys);
    }

    [Fact]
    public void GetProbeKeys_preserves_rewritten_title_for_reverse_filename_entries()
    {
        var m = BuildMatcher();
        var entries = m.GetProbeKeys(null, null, @"X:\drop\Foundation - Isaac Asimov.epub")
            .ToList();

        // The reverse-filename probe carries "Foundation" forward as the title
        // the caller should use when that probe hits.
        Assert.Contains(entries, e => e.Key == "isaac asimov" && e.RewrittenTitle == "Foundation");
    }

    // ---------- multi-author metadata credits ---------------------------------

    [Fact]
    public void Matches_each_author_of_a_joint_metadata_credit()
    {
        // One EPUB dc:creator / MOBI EXTH field often carries BOTH names —
        // the joint string matches nobody, the individual names must.
        var m = BuildMatcher();
        var r = m.Resolve("Ari Mercer & Tobin Quist", null, @"X:\drop\book.mobi");
        Assert.NotNull(r);
        Assert.Equal("Ari Mercer", r!.Entry.DisplayName);

        var semi = m.Resolve("Tobin Quist; Elin Ward", null, @"X:\drop\book.epub");
        Assert.NotNull(semi);
        Assert.Equal("Elin Ward", semi!.Entry.DisplayName);
    }

    // ---------- FilenameGuesser-backed probes ----------------------------------

    [Fact]
    public void Matches_author_from_title_by_author_filename()
    {
        var m = BuildMatcher();
        var r = m.Resolve(null, null, @"X:\drop\The Quiet Lantern by Ari Mercer.txt");
        Assert.NotNull(r);
        Assert.Equal("Ari Mercer", r!.Entry.DisplayName);
        Assert.Equal("The Quiet Lantern", r.RewrittenTitle);
    }

    [Fact]
    public void Matches_author_despite_et_al_and_format_tag()
    {
        var m = BuildMatcher();
        var r = m.Resolve(null, null, @"X:\drop\Patchwork Skies - Elin Ward et al_ (mobi).mobi");
        Assert.NotNull(r);
        Assert.Equal("Elin Ward", r!.Entry.DisplayName);
    }

    [Fact]
    public void Matches_inverted_author_with_bracketed_series_segment()
    {
        var m = BuildMatcher();
        var r = m.Resolve(null, null, @"X:\drop\[Cinder Vale 02] - The Glass Orchard - Mercer, Ari.epub");
        Assert.NotNull(r);
        Assert.Equal("Ari Mercer", r!.Entry.DisplayName);
    }
}

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
        Tracked("Arthur C. Clarke"),
        Tracked("Piers Anthony", folder: "Anthony, Piers"),
        Ol("Isaac Asimov"),
        Ol("Ursula K. Le Guin"),
        Ol("Jüan García"),
    });

    // ---------- forward "Author - Title.ext" filename matching ---------------

    [Fact]
    public void Matches_tracked_author_from_forward_filename()
    {
        var m = BuildMatcher();
        var r = m.Resolve(null, null, @"X:\drop\Arthur C. Clarke - Rendezvous with Rama.epub");

        Assert.NotNull(r);
        Assert.True(r!.Entry.IsTracked);
        Assert.Equal("Arthur C. Clarke", r.Entry.FolderName);
        Assert.Null(r.RewrittenTitle);
    }

    [Fact]
    public void Matches_OpenLibrary_author_from_forward_filename()
    {
        var m = BuildMatcher();
        var r = m.Resolve(null, null, @"X:\drop\Isaac Asimov - Foundation.epub");

        Assert.NotNull(r);
        Assert.False(r!.Entry.IsTracked);
        Assert.Equal("Isaac Asimov", r.Entry.FolderName);
    }

    // ---------- reverse "Title - Author.ext" filename matching ---------------

    [Fact]
    public void Matches_tracked_author_from_reverse_filename()
    {
        var m = BuildMatcher();
        var r = m.Resolve(null, null, @"X:\drop\Rendezvous with Rama - Arthur C. Clarke.epub");

        Assert.NotNull(r);
        Assert.True(r!.Entry.IsTracked);
        Assert.Equal("Rendezvous with Rama", r.RewrittenTitle);
    }

    [Fact]
    public void Matches_OpenLibrary_author_from_reverse_filename()
    {
        var m = BuildMatcher();
        var r = m.Resolve(null, null, @"X:\drop\Foundation - Isaac Asimov.mobi");

        Assert.NotNull(r);
        Assert.False(r!.Entry.IsTracked);
        Assert.Equal("Isaac Asimov", r.Entry.FolderName);
        Assert.Equal("Foundation", r.RewrittenTitle);
    }

    [Fact]
    public void Reverse_filename_splits_on_last_dash_so_titles_with_dashes_work()
    {
        var m = BuildMatcher();
        // "Book - Part 2 - Author" — the author lives AFTER the last " - ".
        var r = m.Resolve(null, null, @"X:\drop\Book - Part 2 - Arthur C. Clarke.epub");

        Assert.NotNull(r);
        Assert.Equal("Book - Part 2", r!.RewrittenTitle);
    }

    // ---------- metadata-author matching (dc:creator etc) --------------------

    [Fact]
    public void Matches_tracked_author_from_metadata()
    {
        var m = BuildMatcher();
        var r = m.Resolve("Arthur C. Clarke", null, @"X:\drop\some-opaque-filename.epub");

        Assert.NotNull(r);
        Assert.True(r!.Entry.IsTracked);
    }

    [Fact]
    public void Matches_OpenLibrary_author_from_metadata()
    {
        var m = BuildMatcher();
        var r = m.Resolve("Ursula K. Le Guin", null, @"X:\drop\some-opaque-filename.epub");

        Assert.NotNull(r);
        Assert.False(r!.Entry.IsTracked);
        Assert.Equal("Ursula K. Le Guin", r.Entry.FolderName);
    }

    [Fact]
    public void Comma_form_metadata_matches_space_form_entry()
    {
        // "Last, First" style — NormalizeAuthor flips these before lookup.
        var m = BuildMatcher();
        var r = m.Resolve("Clarke, Arthur C.", null, @"X:\drop\book.epub");

        Assert.NotNull(r);
        Assert.Equal("Arthur C. Clarke", r!.Entry.DisplayName);
    }

    [Fact]
    public void Authorsort_metadata_is_probed_when_primary_author_does_not_match()
    {
        // Kindle files sometimes fill author as display form AND author_sort
        // as "Last, First" separately — both should land on the same entry.
        var m = BuildMatcher();
        var r = m.Resolve(null, "Clarke, Arthur C.", @"X:\drop\book.epub");

        Assert.NotNull(r);
        Assert.Equal("Arthur C. Clarke", r!.Entry.DisplayName);
    }

    [Fact]
    public void Diacritics_are_stripped_so_accented_names_still_match()
    {
        var m = BuildMatcher();
        var r1 = m.Resolve("Juan Garcia", null, @"X:\drop\book.epub");
        var r2 = m.Resolve("Jüan García", null, @"X:\drop\book.epub");

        Assert.NotNull(r1);
        Assert.NotNull(r2);
        Assert.Equal(r1!.Entry.DisplayName, r2!.Entry.DisplayName);
    }

    // ---------- name-variant rotations ---------------------------------------

    [Fact]
    public void Space_form_surname_first_matches_forename_first_entry()
    {
        // Index has "Arthur C. Clarke"; probe "Clarke Arthur C" (Last + rest).
        var m = BuildMatcher();
        var r = m.Resolve("Clarke Arthur C", null, @"X:\drop\book.epub");

        Assert.NotNull(r);
        Assert.Equal("Arthur C. Clarke", r!.Entry.DisplayName);
    }

    [Fact]
    public void ExpandNameVariants_emits_original_and_both_rotations_for_three_tokens()
    {
        var variants = AuthorMatcher.ExpandNameVariants("arthur c clarke").ToList();

        Assert.Contains("arthur c clarke", variants);
        Assert.Contains("clarke arthur c", variants);   // last + rest
        Assert.Contains("c clarke arthur", variants);   // rest + first
    }

    [Fact]
    public void ExpandNameVariants_collapses_rotations_for_two_token_names()
    {
        var variants = AuthorMatcher.ExpandNameVariants("arthur clarke").ToList();

        Assert.Contains("arthur clarke", variants);
        Assert.Contains("clarke arthur", variants);
        // The "rest + first" rotation collapses to the same string and isn't
        // re-emitted; two distinct variants is all we expect.
        Assert.Equal(2, variants.Count);
    }

    // ---------- tracked wins over OL on index collision ----------------------

    [Fact]
    public void Tracked_entry_wins_over_OL_entry_with_same_normalized_key()
    {
        var m = new AuthorMatcher(new[]
        {
            Ol("Arthur C. Clarke"),                  // OL-catalog version
            Tracked("Arthur C. Clarke", folder: "Clarke-Arthur"),
        });
        var r = m.Resolve("Arthur C. Clarke", null, @"X:\drop\book.epub");

        Assert.NotNull(r);
        Assert.True(r!.Entry.IsTracked);
        Assert.Equal("Clarke-Arthur", r.Entry.FolderName);
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

    // ---------- folder-layout ancestor walk ----------------------------------

    [Fact]
    public void Folder_layout_finds_tracked_author_from_ancestor_folder()
    {
        var m = BuildMatcher();
        var (entry, title) = m.ResolveFolderLayout(
            folderPath: @"X:\drop\Arthur C. Clarke\Rendezvous with Rama",
            sourceRoot: @"X:\drop");

        Assert.NotNull(entry);
        Assert.True(entry!.IsTracked);
        Assert.Equal("Rendezvous with Rama", title);
    }

    [Fact]
    public void Folder_layout_finds_OL_author_from_ancestor_folder()
    {
        var m = BuildMatcher();
        var (entry, title) = m.ResolveFolderLayout(
            folderPath: @"X:\drop\Isaac Asimov\Foundation",
            sourceRoot: @"X:\drop");

        Assert.NotNull(entry);
        Assert.False(entry!.IsTracked);
        Assert.Equal("Foundation", title);
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
}

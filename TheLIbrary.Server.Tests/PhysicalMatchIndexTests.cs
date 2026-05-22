using TheLibrary.Server.Services.Import;
using Xunit;

namespace TheLibrary.Server.Tests;

// Direct tests against PhysicalMatchIndex so the matcher's behaviour can be
// verified without spinning up a DbContext. These pin the comma-form and
// surname-first author tolerance — the user-reported case was that an
// unmatched row with Author="Anthony, Piers" wasn't being rematched against
// a DB Book whose Author.Name = "Piers Anthony".
public class PhysicalMatchIndexTests
{
    private static PhysicalMatchIndex Build(params (int Id, string Title, string AuthorName)[] books)
    {
        var rows = books.Select(b => new PhysicalMatchIndex.SourceRow(
            b.Id,
            b.Title,
            // The real LoadAsync reads NormalizedTitle from the DB; here we
            // recompute it the same way Book.NormalizedTitle was originally set.
            TheLibrary.Server.Services.Sync.TitleNormalizer.Normalize(b.Title),
            Isbn: null,
            ManuallyOwned: false,
            AuthorName: b.AuthorName));
        return PhysicalMatchIndex.Build(rows);
    }

    private static PhysicalMatchIndex BuildWithIsbn(int id, string title, string author, string isbn)
        => PhysicalMatchIndex.Build(new[]
        {
            new PhysicalMatchIndex.SourceRow(
                id, title,
                TheLibrary.Server.Services.Sync.TitleNormalizer.Normalize(title),
                isbn, false, author),
        });

    // ── The user's reported case ─────────────────────────────────────────────

    [Fact]
    public void Anthony_comma_Piers_matches_Piers_Anthony()
    {
        var index = Build((101, "Faun and Games", "Piers Anthony"));
        var hit = index.TryMatch("Faun and Games", "Anthony, Piers");
        Assert.NotNull(hit);
        Assert.Equal(101, hit!.Value.Id);
    }

    [Fact]
    public void Piers_Anthony_matches_Anthony_comma_Piers()
    {
        // Mirror image — DB has the Calibre author_sort form, inventory has display form.
        var index = Build((101, "Faun and Games", "Anthony, Piers"));
        var hit = index.TryMatch("Faun and Games", "Piers Anthony");
        Assert.NotNull(hit);
        Assert.Equal(101, hit!.Value.Id);
    }

    [Fact]
    public void Anthony_Piers_no_comma_still_matches_Piers_Anthony()
    {
        var index = Build((101, "Faun and Games", "Piers Anthony"));
        var hit = index.TryMatch("Faun and Games", "Anthony Piers");  // no comma
        Assert.NotNull(hit);
        Assert.Equal(101, hit!.Value.Id);
    }

    // ── Ampersand / "and" title normalisation ────────────────────────────────

    [Fact]
    public void Title_with_ampersand_matches_DB_title_with_and()
    {
        var index = Build((101, "Faun and Games", "Piers Anthony"));
        var hit = index.TryMatch("Faun & Games", "Anthony, Piers");
        Assert.NotNull(hit);
        Assert.Equal(101, hit!.Value.Id);
    }

    // ── Multi-candidate picking ──────────────────────────────────────────────

    [Fact]
    public void Picks_the_candidate_whose_author_variants_overlap()
    {
        // Two books with the same title under different authors. The matcher
        // must pick the one whose author matches the inventory row.
        var index = Build(
            (101, "Foundation", "Isaac Asimov"),
            (202, "Foundation", "Other Author"));
        var hit = index.TryMatch("Foundation", "Asimov, Isaac");
        Assert.NotNull(hit);
        Assert.Equal(101, hit!.Value.Id);
    }

    // ── Multi-word authors with middle initials ──────────────────────────────

    [Fact]
    public void Middle_initial_in_comma_form_matches()
    {
        var index = Build((101, "Rendezvous with Rama", "Arthur C. Clarke"));
        var hit = index.TryMatch("Rendezvous with Rama", "Clarke, Arthur C.");
        Assert.NotNull(hit);
        Assert.Equal(101, hit!.Value.Id);
    }

    // ── Title-mismatch path: returns null, not a wrong-fallback ──────────────

    [Fact]
    public void Title_not_in_index_returns_null()
    {
        var index = Build((101, "Foundation", "Isaac Asimov"));
        var hit = index.TryMatch("Something Else Entirely", "Asimov, Isaac");
        Assert.Null(hit);
    }

    // ── Fuzzy fallback (the unmatched-table rematch) ─────────────────────────

    [Fact]
    public void Fuzzy_matches_a_near_identical_title_for_the_same_author()
    {
        var index = Build((101, "The Colour of Magic", "Terry Pratchett"));
        // Exact + loose lookups both miss on the spelling difference.
        Assert.Null(index.TryMatch("The Color of Magic", "Pratchett, Terry"));
        var hit = index.TryFuzzyMatch("The Color of Magic", "Pratchett, Terry", 0.9);
        Assert.NotNull(hit);
        Assert.Equal(101, hit!.Value.Id);
    }

    [Fact]
    public void Fuzzy_still_requires_the_author_to_match()
    {
        var index = Build((101, "The Colour of Magic", "Terry Pratchett"));
        Assert.Null(index.TryFuzzyMatch("The Color of Magic", "Someone Else", 0.9));
    }

    [Fact]
    public void Fuzzy_rejects_titles_below_the_threshold()
    {
        var index = Build((101, "Mort", "Terry Pratchett"));
        Assert.Null(index.TryFuzzyMatch("Guards! Guards!", "Terry Pratchett", 0.9));
    }

    [Fact]
    public void Fuzzy_does_not_use_the_series_column()
    {
        // The matcher only ever sees title + author — the unmatched row's
        // Series/Pos column is never passed in, so it cannot block a match.
        var index = Build((101, "Small Gods", "Terry Pratchett"));
        var hit = index.TryFuzzyMatch("Small Gods", "Terry Pratchett", 0.9);
        Assert.NotNull(hit);
        Assert.Equal(101, hit!.Value.Id);
    }

    // ── Author is required for a title hit ───────────────────────────────────

    [Fact]
    public void Title_match_with_a_wrong_author_returns_null()
    {
        var index = Build((101, "Foundation", "Isaac Asimov"));
        // Same title, different author — must not auto-resolve to the wrong book.
        Assert.Null(index.TryMatch("Foundation", "Someone Else"));
    }

    // ── ISBN matching ────────────────────────────────────────────────────────

    [Fact]
    public void Isbn_match_wins_even_when_title_and_author_differ()
    {
        var index = BuildWithIsbn(101, "Foundation", "Isaac Asimov", "9780743247221");
        var hit = index.TryMatch("totally wrong title", "Nobody", "978-0-7432-4722-1");
        Assert.NotNull(hit);
        Assert.Equal(101, hit!.Value.Id);
    }

    [Fact]
    public void Isbn_mismatch_falls_back_to_title_and_author()
    {
        var index = BuildWithIsbn(101, "Foundation", "Isaac Asimov", "9780743247221");
        // A different (valid) ISBN — no ISBN hit, so title + author resolve it.
        var hit = index.TryMatch("Foundation", "Asimov, Isaac", "080442957X");
        Assert.NotNull(hit);
        Assert.Equal(101, hit!.Value.Id);
    }
}

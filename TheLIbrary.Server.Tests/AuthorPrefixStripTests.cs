using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

// Covers the author-prefix/suffix stripping that runs before a file's stem is
// fed to FolderTitleCandidates. Without it, "Terry Brooks - Magic Kingdom for
// Sale.epub" would normalise to "terry brooks magic kingdom for sale" and
// never match the actual "Magic Kingdom for Sale" book.
public class AuthorPrefixStripTests
{
    private static Author A(string name, string? folder = null) =>
        new() { Id = 1, Name = name, CalibreFolderName = folder };

    // ── Leading "<Author> - <Title>" forms ───────────────────────────────────

    [Theory]
    [InlineData("Terry Brooks - Magic Kingdom for Sale",
                "Terry Brooks", "Magic Kingdom for Sale")]
    [InlineData("Arthur C. Clarke - Rendezvous with Rama",
                "Arthur C. Clarke", "Rendezvous with Rama")]
    [InlineData("Brooks, Terry - Magic Kingdom for Sale",       // "Last, First" author sort
                "Terry Brooks", "Magic Kingdom for Sale")]
    [InlineData("Brooks Terry - Magic Kingdom",                 // surname-first, no comma
                "Terry Brooks", "Magic Kingdom")]
    public void StripsAuthorPrefix(string stem, string authorName, string expected)
    {
        var result = SyncService.StripAuthorPrefixOrSuffix(stem, A(authorName));
        Assert.Equal(expected, result);
    }

    // ── Trailing "<Title> - <Author>" forms (reverse-filename) ───────────────

    [Theory]
    [InlineData("Rendezvous with Rama - Arthur C. Clarke",
                "Arthur C. Clarke", "Rendezvous with Rama")]
    [InlineData("Magic Kingdom for Sale - Brooks, Terry",
                "Terry Brooks", "Magic Kingdom for Sale")]
    public void StripsAuthorSuffix(string stem, string authorName, string expected)
    {
        var result = SyncService.StripAuthorPrefixOrSuffix(stem, A(authorName));
        Assert.Equal(expected, result);
    }

    // ── No author segment → stem unchanged ───────────────────────────────────

    [Theory]
    [InlineData("Magic Kingdom for Sale", "Terry Brooks")]
    [InlineData("Some Random Title - Some Random Other Author", "Terry Brooks")]
    [InlineData("Just a Title", "Terry Brooks")]
    [InlineData("", "Terry Brooks")]
    public void LeavesStemAlone_WhenNoAuthorSegment(string stem, string authorName)
    {
        var result = SyncService.StripAuthorPrefixOrSuffix(stem, A(authorName));
        Assert.Equal(stem, result);
    }

    // ── CalibreFolderName variants are also recognised ──────────────────────

    [Fact]
    public void StripsCalibreFolderName_WhenItDiffersFromDisplayName()
    {
        // Author.Name "Terry Brooks", CalibreFolderName "Brooks, Terry" —
        // a file prefixed with the folder name still resolves.
        var stem = "Brooks, Terry - The Sword of Shannara";
        var result = SyncService.StripAuthorPrefixOrSuffix(
            stem, A("Terry Brooks", folder: "Brooks, Terry"));
        Assert.Equal("The Sword of Shannara", result);
    }

    // ── Stem ordering: stripped version comes first, raw fallback follows ────

    [Fact]
    public void TitleStemCandidates_StrippedFirst_OriginalAsFallback()
    {
        var list = SyncService
            .TitleStemCandidates("Terry Brooks - Magic Kingdom for Sale", A("Terry Brooks"))
            .ToList();
        Assert.Equal(new[] { "Magic Kingdom for Sale", "Terry Brooks - Magic Kingdom for Sale" }, list);
    }

    [Fact]
    public void TitleStemCandidates_NoMatch_YieldsOnlyOriginal()
    {
        var list = SyncService
            .TitleStemCandidates("Magic Kingdom for Sale", A("Terry Brooks"))
            .ToList();
        Assert.Single(list);
        Assert.Equal("Magic Kingdom for Sale", list[0]);
    }

    [Fact]
    public void TitleStemCandidates_EmptyStem_YieldsNothing()
    {
        var list = SyncService.TitleStemCandidates("", A("Terry Brooks")).ToList();
        Assert.Empty(list);
    }

    // ── Series-filename grammar adds the parsed title as a candidate ─────────

    [Fact]
    public void TitleStemCandidates_SeriesGrammar_AddsBareTitle()
    {
        // "Heechee 6 - The Boy Who Would Live Forever" should yield the bare
        // title as a separate candidate (in addition to the raw stem).
        var list = SyncService
            .TitleStemCandidates("Heechee 6 - The Boy Who Would Live Forever", A("Frederik Pohl"))
            .ToList();
        Assert.Contains("The Boy Who Would Live Forever", list);
        Assert.Contains("Heechee 6 - The Boy Who Would Live Forever", list);
    }

    [Fact]
    public void TitleStemCandidates_AuthorPrefixPlusSeries_AddsBoth()
    {
        // "Pohl, Frederik - Heechee 6 - The Boy" — author strip + series parse
        // should both contribute candidates.
        var list = SyncService
            .TitleStemCandidates("Pohl, Frederik - Heechee 6 - The Boy", A("Frederik Pohl"))
            .ToList();
        Assert.Contains("The Boy", list);
        // The author-stripped form is also present so non-series titles match.
        Assert.Contains("Heechee 6 - The Boy", list);
    }

    // ── Long stem with multiple dashes: only the right-end segment strip is
    //    considered for the suffix case, not interior splits.

    [Fact]
    public void Multi_Dash_Title_NotConfusedWithAuthor()
    {
        // "Book - Part 2 - Author at end" → strip suffix, title becomes
        // "Book - Part 2"
        var result = SyncService.StripAuthorPrefixOrSuffix(
            "Book - Part 2 - Arthur C. Clarke", A("Arthur C. Clarke"));
        Assert.Equal("Book - Part 2", result);
    }

    [Fact]
    public void Author_Prefix_With_Multi_Dash_Title()
    {
        // "<Author> - Title with - extra dashes" → strip the FIRST " - ",
        // title is everything after.
        var result = SyncService.StripAuthorPrefixOrSuffix(
            "Terry Brooks - The Magic Kingdom - Special Edition", A("Terry Brooks"));
        Assert.Equal("The Magic Kingdom - Special Edition", result);
    }
}

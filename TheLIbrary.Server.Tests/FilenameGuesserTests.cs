using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

// Each case mirrors the SHAPE of a real filename from the live quarantine
// folder (the guesser exists because those yielded no content-based guess at
// all), with invented book and author names standing in for the real ones.
public class FilenameGuesserTests
{
    [Fact]
    public void Title_By_Author_Single_Segment()
    {
        var guesses = FilenameGuesser.Interpret(@"/Books/TheLibrary_Unknown/The Glimmer by Arnold C. Quibble.txt");
        Assert.Contains(guesses, g => g.Author == "Arnold C. Quibble" && g.Title == "The Glimmer");
    }

    [Fact]
    public void Title_Dash_Author_Is_The_First_Interpretation()
    {
        var guesses = FilenameGuesser.Interpret(@"/Books/TheLibrary_Unknown/Murder Most Plaid - Leopold Cribbins.azw3");
        Assert.Equal("Leopold Cribbins", guesses[0].Author);
        Assert.Equal("Murder Most Plaid", guesses[0].Title);
        // The opposite orientation is still offered — the catalogue check decides.
        Assert.Contains(guesses, g => g.Author == "Murder Most Plaid" && g.Title == "Leopold Cribbins");
    }

    [Fact]
    public void Author_Dash_Title_Is_Offered_Too()
    {
        var guesses = FilenameGuesser.Interpret(@"/Books/TheLibrary_Unknown/A. N. Pringle - The Gadget Questions.mobi");
        Assert.Contains(guesses, g => g.Author == "A. N. Pringle" && g.Title == "The Gadget Questions");
    }

    [Fact]
    public void Inverted_Name_With_Series_Segment()
    {
        var guesses = FilenameGuesser.Interpret(
            @"/Books/TheLibrary_Unknown/Marlow, Gregor TT - Frost and Flame 00 - The Ditch Squire.lit");
        var g = guesses[0];
        Assert.Equal("Gregor TT Marlow", g.Author);
        Assert.Equal("The Ditch Squire", g.Title);
        Assert.Equal("Frost and Flame", g.Series);
        Assert.Equal("0", g.SeriesPosition);
    }

    [Fact]
    public void Multi_Author_Keeps_The_First_Author_Whole()
    {
        var guesses = FilenameGuesser.Interpret(
            @"/Books/TheLibrary_Unknown/The Spectre Club - Sven Nickleby;J. R. Drizzle.mobi");
        Assert.Equal("Sven Nickleby", guesses[0].Author);
        Assert.Equal("The Spectre Club", guesses[0].Title);
    }

    [Fact]
    public void Bare_First_Name_Borrows_The_CoAuthors_Surname()
    {
        // "Derren; Stella Gormwell" — the shared surname is listed once, at the end.
        var guesses = FilenameGuesser.Interpret(
            @"/Books/TheLibrary_Unknown/03 - Fall of Quills - Derren; Stella Gormwell.mobi");
        Assert.Contains(guesses, g => g.Author == "Derren Gormwell" && g.Title == "Fall of Quills" && g.SeriesPosition == "3");
    }

    [Fact]
    public void Et_Al_And_Trailing_Underscore_Are_Stripped()
    {
        var guesses = FilenameGuesser.Interpret(
            @"/Books/TheLibrary_Unknown/Swords of Tin Omnibus - Horvat K Bindlestiff et al_.mobi");
        Assert.Equal("Horvat K Bindlestiff", guesses[0].Author);
    }

    [Fact]
    public void Leading_Series_Tag_And_Format_Tag()
    {
        var guesses = FilenameGuesser.Interpret(
            @"/Books/TheLibrary_Unknown/[Three Speculators 13] - The Secret of the Crooked Hat - Wilbur Ardent (mobi).mobi");
        Assert.Contains(guesses, g =>
            g.Author == "Wilbur Ardent" && g.Title == "The Secret of the Crooked Hat"
            && g.Series == "Three Speculators" && g.SeriesPosition == "13");
    }

    [Fact]
    public void Inverted_Article_Is_A_Title_Not_An_Author()
    {
        var guesses = FilenameGuesser.Interpret(@"/Books/TheLibrary_Unknown/AE Von Vimble - Bramblemane, The.txt");
        Assert.Contains(guesses, g => g.Author == "AE Von Vimble" && g.Title == "The Bramblemane");
    }

    [Fact]
    public void Underscore_Reads_As_The_Sanitised_Colon()
    {
        var guesses = FilenameGuesser.Interpret(
            @"/Books/TheLibrary_Unknown/Honest Plan_ A Capable Romance - Tyla Wobbler.azw3");
        Assert.Equal("Tyla Wobbler", guesses[0].Author);
        Assert.Equal("Honest Plan: A Capable Romance", guesses[0].Title);
    }

    [Fact]
    public void Series_Position_Without_Author_Still_Yields_Title_And_Series()
    {
        var guesses = FilenameGuesser.Interpret(@"/Books/TheLibrary_Unknown/Pegworth 1 - Get Off The Turnstile.txt");
        Assert.Contains(guesses, g =>
            g.Author == null && g.Title == "Get Off The Turnstile" && g.Series == "Pegworth" && g.SeriesPosition == "1");
    }

    [Fact]
    public void Series_Tag_In_The_Middle_Claims_Series_And_Redundant_Name_Part()
    {
        var guesses = FilenameGuesser.Interpret(
            @"/x/The Three Speculators - [Three Speculators 34] - The Mystery of the Wandering Hen - M V Carbuncle (mobi).mobi");
        Assert.Equal("M V Carbuncle", guesses[0].Author);
        Assert.Equal("The Mystery of the Wandering Hen", guesses[0].Title);
        Assert.Equal("Three Speculators", guesses[0].Series);
        Assert.Equal("34", guesses[0].SeriesPosition);
    }

    [Fact]
    public void Title_Fragment_After_An_And_Split_Is_Not_A_Mononym_Author()
    {
        // "Mud and Cupcakes" must never yield author "Mud".
        var guesses = FilenameGuesser.Interpret(@"/x/Mud and Cupcakes - Tak McCovey El.azw3");
        Assert.DoesNotContain(guesses, g => g.Author == "Mud");
        Assert.Contains(guesses, g => g.Author == "Tak McCovey El" && g.Title == "Mud and Cupcakes");
    }

    [Fact]
    public void Stacked_Format_Tags_Are_Stripped_From_Titles()
    {
        var guesses = FilenameGuesser.Interpret(
            @"/x/K A Plimsoll & Christine Plume & Cate Dunworthy - The Spectral 13 (retail) (azw3).azw3");
        Assert.Contains(guesses, g => g.Author == "K A Plimsoll" && g.Title == "The Spectral 13");
    }

    [Fact]
    public void Placeholder_Segment_Is_Dropped_So_The_By_Split_Still_Works()
    {
        // "fiction by ROBERT F. YOUNG - Unknown.txt" shape: the placeholder
        // hides the single-segment "by" pattern.
        var guesses = FilenameGuesser.Interpret(@"/x/fiction by Arnold C. Quibble - Unknown.txt");
        Assert.Contains(guesses, g => g.Author == "Arnold C. Quibble" && g.Title == "fiction");
    }

    [Fact]
    public void Smashed_Title_And_Author_Probes_Trailing_Word_Groups()
    {
        // "Almuric Robert E. Howard - Unknown.txt" shape: title and author in
        // one segment with no separator at all.
        var guesses = FilenameGuesser.Interpret(@"/x/Almara Robert E. Quibble - Unknown.txt");
        Assert.Contains(guesses, g => g.Author == "Robert E. Quibble" && g.Title == "Almara");
    }

    [Fact]
    public void Trailing_Parenthesised_Author_Is_The_First_Guess()
    {
        // "Inferno (Troy Denning).txt" shape.
        var guesses = FilenameGuesser.Interpret(@"/x/Inferno Quest (Troy Denwick).txt");
        Assert.Equal("Troy Denwick", guesses[0].Author);
        Assert.Equal("Inferno Quest", guesses[0].Title);
    }

    [Fact]
    public void Hyphen_Without_Spaces_Splits_Author_From_Title()
    {
        // "Charles L. Harness-Lethary Fair - Unknown.txt" shape.
        var guesses = FilenameGuesser.Interpret(@"/x/Charles L. Harwick-Lethary Fair - Unknown.txt");
        Assert.Contains(guesses, g => g.Author == "Charles L. Harwick" && g.Title == "Lethary Fair");
    }

    [Fact]
    public void Placeholder_Words_Are_Never_Offered_As_Authors()
    {
        var guesses = FilenameGuesser.Interpret(@"/x/Some Curious Story - Unknown.txt");
        Assert.DoesNotContain(guesses, g => g.Author == "Unknown");
        var anon = FilenameGuesser.Interpret(@"/x/Collected Oddments - Anonymous.txt");
        Assert.DoesNotContain(anon, g => g.Author == "Anonymous");
    }

    [Fact]
    public void Junk_Filenames_Yield_Nothing_Usable()
    {
        Assert.Empty(FilenameGuesser.Interpret(@"/Books/TheLibrary_Unknown/CMakeLists_10.txt"));
        Assert.Empty(FilenameGuesser.Interpret(@"/Books/TheLibrary_Unknown/ssrstw16.txt"));
        // URL-bearing names produce no author.
        var url = FilenameGuesser.Interpret(@"/Books/TheLibrary_Unknown/Eva by Aldous Blackbird http___w - http___www.spookmasters.com.mobi");
        Assert.DoesNotContain(url, g => g.Author != null && g.Author.Contains("http"));
    }
}

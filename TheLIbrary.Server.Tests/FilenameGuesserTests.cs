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
    public void Leading_Ebook_Tag_Is_Stripped()
    {
        // "(ebook) Gene Wolfe - Death of Dr Island.txt" shape.
        var guesses = FilenameGuesser.Interpret(@"/x/(ebook) Arnold C. Quibble - Death of Dr Mango.txt");
        Assert.Contains(guesses, g => g.Author == "Arnold C. Quibble" && g.Title == "Death of Dr Mango");
    }

    [Fact]
    public void Truncated_Ebook_By_Group_Segment_Is_Dropped()
    {
        // "01 - Empire in Chaos - Anthony Reynolds - (ebook by Un.mobi" shape —
        // the release-group tag is cut off mid-word by the 30-char rename.
        var guesses = FilenameGuesser.Interpret(@"/x/01 - Empire in Chaos - Arnold C. Quibble - (ebook by Un.mobi");
        Assert.Contains(guesses, g => g.Author == "Arnold C. Quibble" && g.Title == "Empire in Chaos");
    }

    [Fact]
    public void By_Author_Split_Works_Inside_A_Multi_Segment_Name()
    {
        // "02 - The Cloud-Sculptors of Coral D by J. G. Ballard.txt" shape.
        var guesses = FilenameGuesser.Interpret(@"/x/02 - The Cloud-Carvers of Coral Z by J. G. Quibble.txt");
        Assert.Contains(guesses, g => g.Author == "J. G. Quibble" && g.Title == "The Cloud-Carvers of Coral Z");
    }

    [Fact]
    public void Editor_Tag_Is_Stripped_From_The_Author()
    {
        // "Adam Millard (ed) - Wake Up Dead.mobi" shape.
        var guesses = FilenameGuesser.Interpret(@"/x/Adam Quibble (ed) - Wake Up Dazed.mobi");
        Assert.Contains(guesses, g => g.Author == "Adam Quibble" && g.Title == "Wake Up Dazed");
    }

    [Fact]
    public void Each_CoAuthor_Of_A_Joint_Credit_Is_Offered()
    {
        // "Adriana Campoy & James P. Blaylock - Stone Eggs.mobi" shape — the
        // second author may be the catalogue-known one.
        var guesses = FilenameGuesser.Interpret(@"/x/Adriana Plimsoll & James P. Quibble - Stone Eggs.mobi");
        Assert.Contains(guesses, g => g.Author == "Adriana Plimsoll" && g.Title == "Stone Eggs");
        Assert.Contains(guesses, g => g.Author == "James P. Quibble" && g.Title == "Stone Eggs");
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

    [Fact]
    public void Inverted_Name_Series_Tag_Version_And_Square_Bracket_Format_Tag_Are_All_Handled()
    {
        // Real-world shape: "Bradley, Marion Zimmer - [Darkover 06] - A Flame In Hali (v1.0) [rtf]_1.lit"
        // Author is inverted (Last, First Middle), series tag is in the middle,
        // version tag "(v1.0)" is in round brackets, format tag "[rtf]" is in
        // square brackets, and "_1" is a Calibre duplicate suffix.
        var guesses = FilenameGuesser.Interpret(
            @"/incoming/Quibble, Marion Anne - [Frost Series 06] - A Flame In Vance (v1.0) [rtf]_1.lit");
        Assert.Contains(guesses, g =>
            g.Author == "Marion Anne Quibble"
            && g.Title == "A Flame In Vance"
            && g.Series == "Frost Series"
            && g.SeriesPosition == "6");
    }

    [Fact]
    public void Square_Bracket_Format_Tag_Is_Stripped_From_Title()
    {
        // "[epub]" in square brackets must be treated the same as "(epub)" in round brackets.
        var guesses = FilenameGuesser.Interpret(
            @"/x/Quibble, Arnold C - The Desert Storm [epub].epub");
        Assert.Contains(guesses, g =>
            g.Author == "Arnold C Quibble" && g.Title == "The Desert Storm");
    }

    // ── New pattern tests ──────────────────────────────────────────────────

    [Fact]
    public void Bare_Editor_Suffix_Is_Stripped_From_Author()
    {
        // "Bradley, Marion Zimmer Ed. - Sword and Sorceress 11.lit"
        // The bare " Ed." suffix must not contaminate the author key so the
        // inverted name resolves to "Marion Zimmer Bradley", not something with "ed" in it.
        var guesses = FilenameGuesser.Interpret(
            @"/x/Bradley, Marion Zimmer Ed. - Sword and Sorceress 11.lit");
        Assert.Contains(guesses, g => g.Author == "Marion Zimmer Bradley");
    }

    [Fact]
    public void Inverted_CoAuthor_Pair_Both_Authors_Probed()
    {
        // "Bradley, Marion Zimmer & Lackey, Mercedes - Darkover 12 - First Age 1 - Rediscovery.pdf"
        // Both co-authors in "Last, First" form must be offered independently.
        var guesses = FilenameGuesser.Interpret(
            @"/x/Bradley, Marion Zimmer & Lackey, Mercedes - Darkover 12 - First Age 1 - Rediscovery.pdf");
        Assert.Contains(guesses, g => g.Author == "Marion Zimmer Bradley");
        Assert.Contains(guesses, g => g.Author == "Mercedes Lackey");
    }

    [Fact]
    public void Two_Part_Inverted_CoAuthor_Both_Authors_Probed()
    {
        // Two-part form: "Aldiss, Brian W & Penrose, Roger - White Mars"
        var guesses = FilenameGuesser.Interpret(
            @"/x/Aldiss, Brian W & Penrose, Roger - White Mars.doc");
        Assert.Contains(guesses, g => g.Author == "Brian W Aldiss");
        Assert.Contains(guesses, g => g.Author == "Roger Penrose");
    }

    [Fact]
    public void CamelCase_Author_Token_Is_Split_Into_Words()
    {
        // "AlanDeanFoster-Flinx 01-ForLoveOfMotherNot_v1.2.lit"
        // The CamelCase token "AlanDeanFoster" must expand to "Alan Dean Foster".
        var guesses = FilenameGuesser.Interpret(
            @"/x/AlanDeanFoster-Flinx 01-ForLoveOfMotherNot_v1.2.lit");
        Assert.Contains(guesses, g => g.Author == "Alan Dean Foster");
    }

    [Fact]
    public void Gutenberg_Slug_Author_And_Title_Are_Extracted()
    {
        // "wilde-oscar-1854-1900_an-ideal-husband.pdf" — BNF/Gutenberg slug.
        // Author slug "wilde-oscar-1854-1900" → "Oscar Wilde"; title slug → "An Ideal Husband".
        var guesses = FilenameGuesser.Interpret(
            @"/x/wilde-oscar-1854-1900_an-ideal-husband.pdf");
        Assert.Contains(guesses, g => g.Author == "Oscar Wilde" && g.Title == "An Ideal Husband");
    }

    [Fact]
    public void Truncated_Paren_Title_Right_Segment_Is_Emitted_As_Author()
    {
        // "Betrayal of Innocence (A New Ad - Rebecca King.mobi"
        // The "(" was never closed — the title was truncated at the OS path limit.
        // The right segment after " - " is the author; it must appear as an author
        // guess, and it must be offered BEFORE the reversed orientation so the
        // catalogue probe hits it first.
        var guesses = FilenameGuesser.Interpret(
            @"/x/Betrayal of Innocence (A New Ad - Rebecca King.mobi");
        // Author=Rebecca King must appear as an early guess.
        var authorGuess = guesses.FirstOrDefault(g => g.Author == "Rebecca King");
        Assert.NotNull(authorGuess);
        // It must appear before the guess where "Rebecca King" is a title.
        var reversedIdx = guesses.ToList().FindIndex(g => g.Title == "Rebecca King");
        var authorIdx = guesses.ToList().FindIndex(g => g.Author == "Rebecca King");
        Assert.True(authorIdx < reversedIdx || reversedIdx < 0,
            "Author=Rebecca King guess should appear before Title=Rebecca King");
    }

    [Fact]
    public void Truncated_Paren_Title_Multi_Word_Author_Is_Found()
    {
        // "She Is His Witness (Birth Of He - Michael Todd.epub"
        var guesses = FilenameGuesser.Interpret(
            @"/x/She Is His Witness (Birth Of He - Michael Todd.epub");
        Assert.Contains(guesses, g => g.Author == "Michael Todd");
    }
}

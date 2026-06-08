using TheLibrary.Server.Services.Calibre;
using Xunit;

namespace TheLibrary.Server.Tests;

public class FrontMatterExtractorTests
{
    [Fact]
    public void Parses_Gutenberg_Header()
    {
        var det = FrontMatterExtractor.Parse("Title: The Ship Who Sang\nAuthor: Anne McCaffrey\n\nChapter One\n");
        Assert.Equal("The Ship Who Sang", det.Title);
        Assert.Equal("Anne McCaffrey", det.Author);
    }

    [Fact]
    public void Finds_Labeled_Isbn()
    {
        var det = FrontMatterExtractor.Parse("First published 1969\nISBN: 978-0-345-33871-9\n");
        Assert.Equal("9780345338719", det.Isbn);
    }

    [Fact]
    public void Reads_Author_From_Copyright_Line()
    {
        var det = FrontMatterExtractor.Parse("Some front matter\nCopyright © 1969 by Anne McCaffrey\nAll rights reserved\n");
        Assert.Equal("Anne McCaffrey", det.Author);
    }

    [Fact]
    public void Copyright_Boilerplate_On_The_Same_Line_Is_Trimmed_From_The_Author()
    {
        // "Copyright … by <Name>. All rights reserved" on ONE line previously
        // captured "John Smith. All" — the boilerplate must be dropped.
        var det = FrontMatterExtractor.Parse("Copyright © 2020 by John Smith. All rights reserved.\n");
        Assert.Equal("John Smith", det.Author);
    }

    [Fact]
    public void Author_Initials_Are_Preserved()
    {
        var det = FrontMatterExtractor.Parse("Title: The Hobbit\nAuthor: J.R.R. Tolkien\n");
        Assert.Equal("J.R.R. Tolkien", det.Author);
    }

    [Fact]
    public void Captures_Also_By_List_And_Author()
    {
        var text = "Also by Anne McCaffrey\nDragonflight\nDragonquest\nThe White Dragon\n\nChapter One\n";
        var det = FrontMatterExtractor.Parse(text);
        Assert.Equal("Anne McCaffrey", det.Author);
        Assert.Contains("Dragonflight", det.AlsoByTitles);
        Assert.Contains("The White Dragon", det.AlsoByTitles);
        Assert.DoesNotContain("Chapter One", det.AlsoByTitles); // list stops at the blank line
    }

    [Fact]
    public void Parses_Series_Line()
    {
        var det = FrontMatterExtractor.Parse("Book Three of the Pern Chronicles\n");
        Assert.Equal("Pern", det.Series);
        Assert.Equal("3", det.SeriesPosition);
    }

    [Fact]
    public void Series_Descriptor_On_Title_Page_Is_Not_Taken_As_The_Title()
    {
        // The line above the byline is a "Book N of …" series descriptor, not a
        // title — it must feed Series/SeriesPosition only, never become a bogus
        // guessed title that gets thrown at OpenLibrary.
        var det = FrontMatterExtractor.Parse(
            "Book 2 of the Sword Dancer Series\nby Jennifer Roberson\n");
        Assert.Equal("Sword Dancer", det.Series);
        Assert.Equal("2", det.SeriesPosition);
        Assert.Equal("Jennifer Roberson", det.Author);
        Assert.Null(det.Title);
    }

    [Fact]
    public void Real_Title_Above_A_Series_Descriptor_Is_Still_Found()
    {
        // The descriptor is skipped so the actual title one line further up wins.
        var det = FrontMatterExtractor.Parse(
            "Sword-Singer\nBook 2 of the Sword Dancer Series\nby Jennifer Roberson\n");
        Assert.Equal("Sword-Singer", det.Title);
        Assert.Equal("Jennifer Roberson", det.Author);
    }

    [Fact]
    public void Builds_Series_Catalog_From_Grouped_Bibliography()
    {
        // Real-world layout (G R Jordan): a "Novels by" heading, then for each
        // series a header line (often with a "(Genre)"), a blank line, and the
        // titles in that series, with blank lines between series.
        var text =
            "Novels by G R Jordan\n" +
            "The Highlands and Islands Detective series (Crime)\n" +
            "\n" +
            "Water's Edge\n" +
            "The Bothy\n" +
            "The Horror Weekend\n" +
            "The Small Ferry\n" +
            "Dead at Third Man\n" +
            "The Pirate Club\n" +
            "A Personal Agenda\n" +
            "A Just Punishment\n" +
            "The Numerous Deaths of Santa Claus\n" +
            "Our Gated Community\n" +
            "The Satchel\n" +
            "Culhwch Alpha\n" +
            "Fair Market Value\n" +
            "\n\n" +
            "Kirsten Stewart Thrillers (Thriller)\n" +
            "\n" +
            "A Shot at Democracy\n" +
            "The Hunted Child\n" +
            "\n\n" +
            "The Contessa Munroe Mysteries (Cozy Mystery)\n" +
            "\n" +
            "Corpse Reviver\n";

        var det = FrontMatterExtractor.Parse(text);

        Assert.Equal("G R Jordan", det.Author);
        Assert.Equal(3, det.SeriesCatalog.Count);

        var detective = det.SeriesCatalog[0];
        Assert.Equal("The Highlands and Islands Detective", detective.Series); // trailing "series" dropped
        Assert.Equal("Crime", detective.Genre);
        Assert.Equal(13, detective.Titles.Count);
        Assert.Equal("Water's Edge", detective.Titles[0]);
        Assert.Contains("The Numerous Deaths of Santa Claus", detective.Titles);
        Assert.Contains("Fair Market Value", detective.Titles);

        var thrillers = det.SeriesCatalog[1];
        Assert.Equal("Kirsten Stewart Thrillers", thrillers.Series); // genre word kept in name
        Assert.Equal("Thriller", thrillers.Genre);
        Assert.Equal(new[] { "A Shot at Democracy", "The Hunted Child" }, thrillers.Titles);

        var cozy = det.SeriesCatalog[2];
        Assert.Equal("The Contessa Munroe Mysteries", cozy.Series);
        Assert.Equal("Cozy Mystery", cozy.Genre);
        Assert.Equal(new[] { "Corpse Reviver" }, cozy.Titles);

        // The flat list still holds every title, across all series.
        Assert.Contains("Water's Edge", det.AlsoByTitles);
        Assert.Contains("Corpse Reviver", det.AlsoByTitles);
        Assert.Equal(16, det.AlsoByTitles.Count);
    }

    [Fact]
    public void Merges_Bibliography_Blocks_From_Front_And_Back_Matter()
    {
        // Simulates head+tail text fed together: a chapter up front, then (after
        // the back matter starts) the same series listed twice — a short front
        // teaser and the full back-matter list. They must merge, not duplicate.
        var text =
            "Title Page\n\nChapter One\n\nsome prose here\n\n" +
            "Also by A B Writer\n" +
            "The Quest Saga\n\nThe First Quest\n\n\n" +
            "Also by A B Writer\n" +
            "The Quest Saga (Fantasy)\n\nThe First Quest\nThe Second Quest\nThe Third Quest\n";

        var det = FrontMatterExtractor.Parse(text);

        Assert.Equal("A B Writer", det.Author);
        var saga = Assert.Single(det.SeriesCatalog);          // one merged series, not two
        Assert.Equal("The Quest Saga", saga.Series);
        Assert.Equal("Fantasy", saga.Genre);                  // genre picked up from the block that had it
        Assert.Equal(new[] { "The First Quest", "The Second Quest", "The Third Quest" }, saga.Titles);
        Assert.Equal(3, det.AlsoByTitles.Count);              // flat list deduped too
    }

    [Fact]
    public void Parses_Full_GRJordan_List_Including_Punctuated_Titles()
    {
        // The exact "Books by" list from McCloud's Cruise — note "Man Overboard!"
        // and the final "Antisocial Behaviour." (trailing period) which used to be
        // dropped and to truncate the whole list.
        var text =
            "Books by G R Jordan\n" +
            "The Highlands and Islands Detective series (Crime)\n\n" +
            "Water's Edge\nThe Bothy\nThe Horror Weekend\nThe Small Ferry\nDead at Third Man\n" +
            "The Pirate Club\nA Personal Agenda\nA Just Punishment\nThe Numerous Deaths of Santa Claus\n" +
            "Our Gated Community\nThe Satchel\nCulhwch Alpha\nFair Market Value\nThe Coach Bomber\n" +
            "The Culling at Singing Sands\nWhere Justice Fails\nThe Cortado Club\nCleared to Die\n" +
            "Man Overboard!\nAntisocial Behaviour.\n";

        var det = FrontMatterExtractor.Parse(text);

        Assert.Equal("G R Jordan", det.Author);
        var s = Assert.Single(det.SeriesCatalog);
        Assert.Equal("The Highlands and Islands Detective", s.Series);
        Assert.Equal("Crime", s.Genre);
        Assert.Equal(20, s.Titles.Count);
        Assert.Equal("Water's Edge", s.Titles[0]);
        Assert.Equal("Man Overboard!", s.Titles[18]);          // "!" preserved
        Assert.Equal("Antisocial Behaviour", s.Titles[19]);    // trailing "." stripped, not dropped
    }

    [Fact]
    public void Stops_AlsoBy_List_Before_Prose_And_Section_Headings()
    {
        // Real Dave Duncan .txt: a clean title list, then a "Warning" note,
        // dedication and epigraph — all hard-wrapped into short lines that the old
        // length-based check mistook for titles.
        var text =
            "Also by Dave Duncan\n" +
            "The King's Blades\nThe Gilded Chain\nThe Great Game\n" +
            "Past Imperative\nPresent Tense\nFuture Indefinite\n" +
            "Warning\n" +
            "This book, like The Gilded Chain, is a\n" +
            "stand-alone novel. They both cover much the same\n" +
            "time interval and certain characters appear in both\n" +
            "This one is for\nSamuel Joseph Duncan\n";

        var det = FrontMatterExtractor.Parse(text);

        Assert.Equal(new[]
        {
            "The King's Blades", "The Gilded Chain", "The Great Game",
            "Past Imperative", "Present Tense", "Future Indefinite",
        }, det.AlsoByTitles);
        Assert.DoesNotContain("Warning", det.AlsoByTitles);
        Assert.DoesNotContain(det.AlsoByTitles, t => t.Contains("stand-alone"));
        Assert.DoesNotContain(det.AlsoByTitles, t => t.StartsWith("This book"));
    }

    [Fact]
    public void Empty_Text_Yields_Nothing()
    {
        var det = FrontMatterExtractor.Parse("   ");
        Assert.False(det.HasAnything);
    }
}

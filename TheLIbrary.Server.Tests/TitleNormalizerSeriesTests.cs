using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

// Covers TitleNormalizer.TryParseSeriesFilename across every filename pattern
// observed in the live LocalBookFiles table. Each section pins one shape and
// uses the production parser end-to-end so behaviour and edge cases stay in sync.
public class TitleNormalizerSeriesTests
{
    // ── Pattern A: "{Series N} - {Title}" — series first, no author ──────────

    [Theory]
    [InlineData("Heechee 6 - The Boy Who Would Live Forever",       "Heechee", "6", "The Boy Who Would Live Forever")]
    [InlineData("Chaoswar Saga 03 - Magician's End",                "Chaoswar Saga", "3", "Magician's End")]
    [InlineData("Midkemia 16 - Legends of The Ri",                  "Midkemia", "16", "Legends of The Ri")]
    [InlineData("Sharpe 21 - Sharpe's Waterloo",                    "Sharpe", "21", "Sharpe's Waterloo")]
    [InlineData("Halliday 13",                                      null, null, null)]
    public void SeriesFirst_NoAuthor(string stem, string? series, string? pos, string? title)
    {
        var (s, p, t, a) = TitleNormalizer.TryParseSeriesFilename(stem);
        Assert.Equal(series, s);
        Assert.Equal(pos, p);
        Assert.Equal(title, t);
        Assert.Null(a);
    }

    // ── Pattern B: "{Series N} - {Title} - {Author}" — author last ───────────

    [Theory]
    [InlineData("Wheel of Time 10 - Crossroads of Twilight - Robert Jordan",
                "Wheel of Time", "10", "Crossroads of Twilight", "Robert Jordan")]
    [InlineData("Xanth 21 - Faun & Games - Piers Anthony",
                "Xanth", "21", "Faun & Games", "Piers Anthony")]
    [InlineData("The Destroyer 058 - Total Recall - Warren Murphy",
                "The Destroyer", "58", "Total Recall", "Warren Murphy")]
    [InlineData("Sherlock Holmes 01 - The Breath - Guy Adams",
                "Sherlock Holmes", "1", "The Breath", "Guy Adams")]
    public void SeriesFirst_WithAuthor(string stem, string series, string pos, string title, string author)
    {
        var (s, p, t, a) = TitleNormalizer.TryParseSeriesFilename(stem);
        Assert.Equal(series, s);
        Assert.Equal(pos, p);
        Assert.Equal(title, t);
        Assert.Equal(author, a);
    }

    // ── Pattern C: "{Author} - {Series N} - {Title}" — author first ──────────

    [Theory]
    [InlineData("Pohl, Frederik - Heechee 6 - The Boy Who Would Live Forever",
                "Heechee", "6", "The Boy Who Would Live Forever", "Pohl, Frederik")]
    [InlineData("Foster, Alan Dean - Catechist 1 - Carnivores of Light and Darkness",
                "Catechist", "1", "Carnivores of Light and Darkness", "Foster, Alan Dean")]
    [InlineData("Feehan, Christine - Dark 07 - Dark Dream",
                "Dark", "7", "Dark Dream", "Feehan, Christine")]
    [InlineData("Feehan, Christine - Dark 00 - The Scarletti Curse",
                "Dark", "0", "The Scarletti Curse", "Feehan, Christine")]
    [InlineData("Bujold, Lois McMaster - Vorkosigan 02 - Barrayar",
                "Vorkosigan", "2", "Barrayar", "Bujold, Lois McMaster")]
    [InlineData("K. A. Applegate - Everworld 07 - Gateway To The Gods",
                "Everworld", "7", "Gateway To The Gods", "K. A. Applegate")]
    public void AuthorFirst_SeriesMiddle(string stem, string series, string pos, string title, string author)
    {
        var (s, p, t, a) = TitleNormalizer.TryParseSeriesFilename(stem);
        Assert.Equal(series, s);
        Assert.Equal(pos, p);
        Assert.Equal(title, t);
        Assert.Equal(author, a);
    }

    // Author with trailing dot/underscore noise is cleaned.
    [Theory]
    [InlineData("Crozier, J.L.; - Some Series 3 - Title",            "Crozier, J.L.;")]
    [InlineData("Wakefield, Trevor. - Series 1 - Title",             "Wakefield, Trevor")]
    [InlineData("Feist, Raymond E_ - Chaoswar Saga 03 - Magician's End", "Feist, Raymond E")]
    public void AuthorTrailingPunctuationStripped(string stem, string expectedAuthor)
    {
        var (_, _, _, a) = TitleNormalizer.TryParseSeriesFilename(stem);
        Assert.Equal(expectedAuthor, a);
    }

    // ── Pattern D: Position lives in its own " - " segment ───────────────────

    [Theory]
    [InlineData("Star Trek_ The Next Generation - 069 - Insurrection",
                "Star Trek_ The Next Generation", "69", "Insurrection", null)]
    [InlineData("Star Trek_ The Original Series - 072 - The Starship Trap",
                "Star Trek_ The Original Series", "72", "The Starship Trap", null)]
    [InlineData("Star Trek_ I.K.S. Gorkon - 003 - Keith R. A. Decandido",
                "Star Trek_ I.K.S. Gorkon", "3", "Keith R. A. Decandido", null)]
    [InlineData("Star Wars - 008 - Lost Tribe of - John Jackson Miller",
                "Star Wars", "8", "Lost Tribe of", "John Jackson Miller")]
    public void PositionAsOwnSegment(string stem, string series, string pos, string title, string? author)
    {
        var (s, p, t, a) = TitleNormalizer.TryParseSeriesFilename(stem);
        Assert.Equal(series, s);
        Assert.Equal(pos, p);
        Assert.Equal(title, t);
        Assert.Equal(author, a);
    }

    // Nested series — the deeper "Subseries N" anchor should win over the bare
    // parent index. The bare parent index "311" must not be treated as an author.
    [Fact]
    public void NestedSeries_DeeperAnchorWins()
    {
        var (s, p, t, a) = TitleNormalizer.TryParseSeriesFilename(
            "Star Wars - 311 - Fate of the Jedi 03 - Abyss");
        Assert.Equal("Fate of the Jedi", s);
        Assert.Equal("3", p);
        Assert.Equal("Abyss", t);
        Assert.Null(a);
    }

    // ── Pattern E: Bracket-prefixed series in first segment ──────────────────

    [Theory]
    [InlineData("[Lorien Legacies 06.0] The Fate - Pittacus Lore",
                "Lorien Legacies", "6", "The Fate", "Pittacus Lore")]
    [InlineData("[Blood Angels 01]Deus Encarmine",
                null, null, null, null)]  // no space after ] and no " - " → 1 part total
    [InlineData("[Dorothy Parker 04] - Death Rid - Agata Stanford",
                "Dorothy Parker", "4", "Death Rid", "Agata Stanford")]
    public void BracketedSeriesPrefix(string stem, string? series, string? pos, string? title, string? author)
    {
        var (s, p, t, a) = TitleNormalizer.TryParseSeriesFilename(stem);
        Assert.Equal(series, s);
        Assert.Equal(pos, p);
        Assert.Equal(title, t);
        Assert.Equal(author, a);
    }

    // ── Pattern F: "{Author} - [{Series N}] - {Title}" — bracketed middle ────

    [Theory]
    [InlineData("Marta Perry - [Watcher in the Dark 05] - When Secrets Strike",
                "Watcher in the Dark", "5", "When Secrets Strike", "Marta Perry")]
    [InlineData("Rob Kidd - [Jack Sparrow 02] - The Siren Song",
                "Jack Sparrow", "2", "The Siren Song", "Rob Kidd")]
    [InlineData("David Gemmell - [Drenai Saga 05] - In the Realm of the Wolf",
                "Drenai Saga", "5", "In the Realm of the Wolf", "David Gemmell")]
    [InlineData("Robert Adams - [Horseclans 02] - Swords of the Horseclans",
                "Horseclans", "2", "Swords of the Horseclans", "Robert Adams")]
    public void BracketedSeriesMiddle(string stem, string series, string pos, string title, string author)
    {
        var (s, p, t, a) = TitleNormalizer.TryParseSeriesFilename(stem);
        Assert.Equal(series, s);
        Assert.Equal(pos, p);
        Assert.Equal(title, t);
        Assert.Equal(author, a);
    }

    // ── Pattern G: "Book"/"Vol"/"Volume"/"Part" keyword in series segment ────
    //
    // When there is exactly one trailing segment after a parts[0]-anchor we can
    // not tell title from author without more context, so the trailing segment
    // is treated as the title and author is left null. Filenames that include a
    // separate title and author (3+ parts) split cleanly.

    [Theory]
    [InlineData("Hank_ Texas Kings MC, Book 11 - Cee Bowerman",
                "Hank_ Texas Kings MC", "11", "Cee Bowerman", null)]
    [InlineData("Holmes of Kyoto_ Volume 6 - Mai Mochizuki",
                "Holmes of Kyoto_", "6", "Mai Mochizuki", null)]
    [InlineData("Spice and Wolf 10 - Isuna Hasekura",
                "Spice and Wolf", "10", "Isuna Hasekura", null)]
    [InlineData("Discworld 11 - Reaper Man - Pratchett, Terry",
                "Discworld", "11", "Reaper Man", "Pratchett, Terry")]
    public void KeywordInsideSeriesSegment(string stem, string series, string pos, string title, string? author)
    {
        var (s, p, t, a) = TitleNormalizer.TryParseSeriesFilename(stem);
        Assert.Equal(series, s);
        Assert.Equal(pos, p);
        Assert.Equal(title, t);
        Assert.Equal(author, a);
    }

    // ── Position normalization: leading zeros stripped, .0 stripped ──────────

    [Theory]
    [InlineData("Series 01 - Title",   "1")]
    [InlineData("Series 003 - Title",  "3")]
    [InlineData("Series 010 - Title",  "10")]
    [InlineData("Series 3.0 - Title",  "3")]
    [InlineData("Series 1.5 - Title",  "1.5")]
    [InlineData("Series 06.5 - Title", "6.5")]
    [InlineData("Series 0 - Title",    "0")]
    public void PositionNormalisation(string stem, string expectedPos)
    {
        var (_, p, _, _) = TitleNormalizer.TryParseSeriesFilename(stem);
        Assert.Equal(expectedPos, p);
    }

    // ── Multi-segment titles: title with embedded " - " is preserved ─────────

    [Fact]
    public void TitleSpanningMultipleSegments_AuthorAtEnd()
    {
        var (s, p, t, a) = TitleNormalizer.TryParseSeriesFilename(
            "Foster 5 - Light - Reckoning - Foster, Alan Dean");
        Assert.Equal("Foster", s);
        Assert.Equal("5", p);
        Assert.Equal("Light - Reckoning", t);
        Assert.Equal("Foster, Alan Dean", a);
    }

    [Fact]
    public void TitleSpanningMultipleSegments_AuthorFirst()
    {
        var (s, p, t, a) = TitleNormalizer.TryParseSeriesFilename(
            "Foster, Alan Dean - Spellsinger 5 - Day of the Dissonance - Special Edition");
        Assert.Equal("Spellsinger", s);
        Assert.Equal("5", p);
        Assert.Equal("Day of the Dissonance - Special Edition", t);
        Assert.Equal("Foster, Alan Dean", a);
    }

    // ── Calibre/tool suffixes stripped from the title ────────────────────────

    [Theory]
    [InlineData("Heechee 6 - The Boy_2",                 "The Boy")]
    [InlineData("Heechee 6 - The Boy_3",                 "The Boy")]
    [InlineData("Heechee 6 - The Boy (123)",             "The Boy")]
    [InlineData("Heechee 6 - The Boy",                   "The Boy")]
    public void CalibreSuffixStrippedFromTitle(string stem, string expectedTitle)
    {
        var (_, _, t, _) = TitleNormalizer.TryParseSeriesFilename(stem);
        Assert.Equal(expectedTitle, t);
    }

    // ── Negative cases — no series anchor present ────────────────────────────

    [Theory]
    [InlineData("The Bracelet")]
    [InlineData("Heinous - Yolanda Olson")]
    [InlineData("01 - Star of Erengrad")]       // bare-number first, no series name
    [InlineData("Just a Title")]
    [InlineData("")]
    [InlineData(null)]
    public void NoSeriesPattern_ReturnsAllNulls(string? stem)
    {
        var (s, p, t, a) = TitleNormalizer.TryParseSeriesFilename(stem);
        Assert.Null(s);
        Assert.Null(p);
        Assert.Null(t);
        Assert.Null(a);
    }

    // ── Bare number must NOT match as a series alone ─────────────────────────

    [Fact]
    public void BareNumber_DoesNotMatchAsSeries()
    {
        var (s, _, _, _) = TitleNormalizer.TryParseSeriesFilename("1984 - George Orwell");
        Assert.Null(s);  // "1984" is a title, not a "Series 1984"
    }

    // ── Preceding bare number is dropped, NOT used as author ─────────────────

    [Fact]
    public void ParentSeriesNumber_NotTreatedAsAuthor()
    {
        var (_, _, _, a) = TitleNormalizer.TryParseSeriesFilename(
            "Star Wars - 311 - Fate of the Jedi 03 - Abyss");
        Assert.Null(a);
    }

    // ── Idempotence — running the parser on its own output series string ────

    [Fact]
    public void Idempotent_OnCleanSeriesName()
    {
        var (s1, _, _, _) = TitleNormalizer.TryParseSeriesFilename("Foundation 3 - Second Foundation");
        var (s2, _, _, _) = TitleNormalizer.TryParseSeriesFilename(s1!);
        Assert.Equal("Foundation", s1);
        Assert.Null(s2);  // "Foundation" alone has no series anchor anymore
    }

    // ── Real-world filenames captured from production DB ─────────────────────

    [Theory]
    [InlineData("Moorcock, Michael - Dancers 01 - An Alien Heat",
                "Dancers", "1", "An Alien Heat", "Moorcock, Michael")]
    [InlineData("Brooks, Terry - High Druid of Shannara 01 - Jarka Ruus",
                "High Druid of Shannara", "1", "Jarka Ruus", "Brooks, Terry")]
    [InlineData("Banks, Iain - Culture 05 - Excession",
                "Culture", "5", "Excession", "Banks, Iain")]
    [InlineData("Anthony, Piers - Cluster 2 - Chaining The Lady",
                "Cluster", "2", "Chaining The Lady", "Anthony, Piers")]
    [InlineData("Pournelle, Jerry - Falkenberg 4 - Prince of Sparta",
                "Falkenberg", "4", "Prince of Sparta", "Pournelle, Jerry")]
    [InlineData("Jacques, Brian - Redwall 02 - Mossflower",
                "Redwall", "2", "Mossflower", "Jacques, Brian")]
    [InlineData("Rex Stout - Nero Wolfe 33 - Too Many Clients",
                "Nero Wolfe", "33", "Too Many Clients", "Rex Stout")]
    [InlineData("Anderson, Poul - Flandry 04 - Let the Spacemen Beware",
                "Flandry", "4", "Let the Spacemen Beware", "Anderson, Poul")]
    [InlineData("Kelvin Knight 03 - Chimaera's C - Piers Anthony",
                "Kelvin Knight", "3", "Chimaera's C", "Piers Anthony")]
    public void RealWorldFilenames(string stem, string series, string pos, string title, string author)
    {
        var (s, p, t, a) = TitleNormalizer.TryParseSeriesFilename(stem);
        Assert.Equal(series, s);
        Assert.Equal(pos, p);
        Assert.Equal(title, t);
        Assert.Equal(author, a);
    }
}

// Covers the supporting helpers TryParseSeriesFilename relies on so a regression
// in either piece surfaces independently.
public class TitleNormalizerSupportingTests
{
    [Theory]
    [InlineData("Foundation",     "foundation")]
    [InlineData("The Foundation", "foundation")]      // leading article stripped
    [InlineData("A Game of Thrones", "game of thrones")]
    [InlineData("An Hour Before Daylight", "hour before daylight")]
    [InlineData("Café — Naïve",   "cafe naive")]      // diacritics + punctuation
    [InlineData("",               "")]
    [InlineData(null,             "")]
    public void Normalize_Cases(string? input, string expected)
    {
        Assert.Equal(expected, TitleNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("Clarke, Arthur C.",   "arthur c clarke")]
    [InlineData("Arthur C. Clarke",    "arthur c clarke")]
    [InlineData("Jüan García",         "juan garcia")]
    [InlineData("",                    "")]
    public void NormalizeAuthor_Cases(string input, string expected)
    {
        Assert.Equal(expected, TitleNormalizer.NormalizeAuthor(input));
    }

    // FolderTitleCandidates yields candidates from most to least specific.
    [Fact]
    public void FolderTitleCandidates_PlainTitle_YieldsOnlyOne()
    {
        var cs = TitleNormalizer.FolderTitleCandidates("Foundation").ToList();
        Assert.Single(cs);
        Assert.Equal("foundation", cs[0]);
    }

    [Fact]
    public void FolderTitleCandidates_TitleWithAuthorParens_YieldsBoth()
    {
        var cs = TitleNormalizer.FolderTitleCandidates("Foundation (Isaac Asimov)").ToList();
        Assert.Equal(new[] { "foundation isaac asimov", "foundation" }, cs);
    }

    [Fact]
    public void FolderTitleCandidates_ByAuthorSuffix_YieldsStripped()
    {
        var cs = TitleNormalizer.FolderTitleCandidates("Foundation by Isaac Asimov").ToList();
        Assert.Contains("foundation by isaac asimov", cs);
        Assert.Contains("foundation", cs);
    }

    [Theory]
    [InlineData("Arthur C. Clarke",  true)]
    [InlineData("Asimov",            true)]
    [InlineData("",                  false)]
    [InlineData("ABS",               false)]   // 3-letter uppercase = blacklisted
    [InlineData("lasg",              false)]   // all-lowercase
    [InlineData(".Brock",            false)]   // leading punctuation
    [InlineData("DS",                false)]   // too short
    public void IsPlausibleAuthorName_Cases(string input, bool expected)
    {
        Assert.Equal(expected, TitleNormalizer.IsPlausibleAuthorName(input));
    }
}

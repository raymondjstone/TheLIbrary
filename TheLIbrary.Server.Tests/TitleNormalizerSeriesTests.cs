using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

// Covers TitleNormalizer.TryParseSeriesFilename across every filename pattern
// observed in representative inventory samples. Each section pins one shape and
// uses the production parser end-to-end so behaviour and edge cases stay in sync.
public class TitleNormalizerSeriesTests
{
    // ── Pattern A: "{Series N} - {Title}" — series first, no author ──────────

    [Theory]
    [InlineData("Deep Range 6 - The Last Beacon",                   "Deep Range", "6", "The Last Beacon")]
    [InlineData("Ashfall Cycle 03 - Ember's End",                  "Ashfall Cycle", "3", "Ember's End")]
    [InlineData("Iron March 16 - Echoes of Glass",                 "Iron March", "16", "Echoes of Glass")]
    [InlineData("Harbor Watch 21 - Winter Signal",                 "Harbor Watch", "21", "Winter Signal")]
    [InlineData("Marchlight 13",                                   null, null, null)]
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
    [InlineData("River of Crowns 10 - Twilight Crossing - Rowan Hale",
                "River of Crowns", "10", "Twilight Crossing", "Rowan Hale")]
    [InlineData("Glass Garden 21 - Fawn & Flame - Mara Voss",
                "Glass Garden", "21", "Fawn & Flame", "Mara Voss")]
    [InlineData("The Resolver 058 - Full Recall - Dorian Pike",
                "The Resolver", "58", "Full Recall", "Dorian Pike")]
    [InlineData("Midnight Ledger 01 - First Breath - S. L. Mercer",
                "Midnight Ledger", "1", "First Breath", "S. L. Mercer")]
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
    [InlineData("Vale, Mira - Deep Range 6 - The Last Beacon",
                "Deep Range", "6", "The Last Beacon", "Vale, Mira")]
    [InlineData("Rowe, Adrian - Lantern Rite 1 - Cities of Salt and Rain",
                "Lantern Rite", "1", "Cities of Salt and Rain", "Rowe, Adrian")]
    [InlineData("Voss, Mara - Night 07 - Silent Dream",
                "Night", "7", "Silent Dream", "Voss, Mara")]
    [InlineData("Voss, Mara - Night 00 - The Scarlet Thread",
                "Night", "0", "The Scarlet Thread", "Voss, Mara")]
    [InlineData("Kestrel, Iona - Meridian 02 - Emberfall",
                "Meridian", "2", "Emberfall", "Kestrel, Iona")]
    [InlineData("Arden Pike - Hollow Worlds 07 - Gate of Ash",
                "Hollow Worlds", "7", "Gate of Ash", "Arden Pike")]
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
    [InlineData("Vale, J.L.; - Some Series 3 - Title",               "Vale, J.L.;")]
    [InlineData("Harrow, Trevor. - Series 1 - Title",                "Harrow, Trevor")]
    [InlineData("Pike, Rowan E_ - Ashfall Cycle 03 - Ember's End",   "Pike, Rowan E")]
    public void AuthorTrailingPunctuationStripped(string stem, string expectedAuthor)
    {
        var (_, _, _, a) = TitleNormalizer.TryParseSeriesFilename(stem);
        Assert.Equal(expectedAuthor, a);
    }

    // ── Pattern D: Position lives in its own " - " segment ───────────────────

    [Theory]
    [InlineData("Galaxy Patrol_ North Wing - 069 - Ember Protocol",
                "Galaxy Patrol_ North Wing", "69", "Ember Protocol", null)]
    [InlineData("Solar Fleet_ First Signal - 072 - The Vessel Trap",
                "Solar Fleet_ First Signal", "72", "The Vessel Trap", null)]
    [InlineData("Void Armada_ Iron Banner - 003 - K. R. Darrington",
                "Void Armada_ Iron Banner", "3", "K. R. Darrington", null)]
    [InlineData("Empire Cycle - 008 - Lost Chorus - Jonah Vale",
                "Empire Cycle", "8", "Lost Chorus", "Jonah Vale")]
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
            "Empire Cycle - 311 - Ashen Banner 03 - Hollow Sky");
        Assert.Equal("Ashen Banner", s);
        Assert.Equal("3", p);
        Assert.Equal("Hollow Sky", t);
        Assert.Null(a);
    }

    // ── Pattern E: Bracket-prefixed series in first segment ──────────────────

    [Theory]
    [InlineData("[Iron Lanterns 06.0] Final Signal - Arden Pike",
                "Iron Lanterns", "6", "Final Signal", "Arden Pike")]
    [InlineData("[Silver Talons 01]Crimson Echo",
                null, null, null, null)]  // no space after ] and no " - " → 1 part total
    [InlineData("[North Station 04] - Last Watch - Ada Wren",
                "North Station", "4", "Last Watch", "Ada Wren")]
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
    [InlineData("Tessa Rowan - [Midnight Archive 05] - Silent Fracture",
                "Midnight Archive", "5", "Silent Fracture", "Tessa Rowan")]
    [InlineData("Nico Ward - [Harbor Ghosts 02] - The Siren Map",
                "Harbor Ghosts", "2", "The Siren Map", "Nico Ward")]
    [InlineData("Elias Marr - [Ashen Crown 05] - In the Wolf Realm",
                "Ashen Crown", "5", "In the Wolf Realm", "Elias Marr")]
    [InlineData("Jonah Pike - [Stone Riders 02] - Blades of the Riders",
                "Stone Riders", "2", "Blades of the Riders", "Jonah Pike")]
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
    [InlineData("Raven_ North Street Crew, Book 11 - Cold Mercy",
                "Raven_ North Street Crew", "11", "Cold Mercy", null)]
    [InlineData("Clockwork Bureau_ Volume 6 - Nina Sato",
                "Clockwork Bureau_", "6", "Nina Sato", null)]
    [InlineData("Salt and Ember 10 - Lena Voss",
                "Salt and Ember", "10", "Lena Voss", null)]
    [InlineData("Night Ledger 11 - Hollow Man - Mercer, Talia",
                "Night Ledger", "11", "Hollow Man", "Mercer, Talia")]
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
    [InlineData("Deep Range 6 - Last Beacon_2",          "Last Beacon")]
    [InlineData("Deep Range 6 - Last Beacon_3",          "Last Beacon")]
    [InlineData("Deep Range 6 - Last Beacon (123)",      "Last Beacon")]
    [InlineData("Deep Range 6 - Last Beacon",            "Last Beacon")]
    public void CalibreSuffixStrippedFromTitle(string stem, string expectedTitle)
    {
        var (_, _, t, _) = TitleNormalizer.TryParseSeriesFilename(stem);
        Assert.Equal(expectedTitle, t);
    }

    // ── Negative cases — no series anchor present ────────────────────────────

    [Theory]
    [InlineData("The Bracelet")]
    [InlineData("Sharp Hollow - Nina Rowan")]
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
        var (s, _, _, _) = TitleNormalizer.TryParseSeriesFilename("1984 - Rowan Vale");
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

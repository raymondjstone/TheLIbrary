using System.Text.RegularExpressions;

namespace TheLibrary.Server.Services.Calibre;

// A single series found in an author's "also by / novels by" bibliography,
// together with the titles listed under it. This is what lets the series
// catalogue be built automatically: each title is attributed to a named series.
public sealed record SeriesListing(
    string Series,
    string? Genre,
    IReadOnlyList<string> Titles);

public sealed record ContentDetermination(
    string? Isbn,
    string? Title,
    string? Author,
    string? Series,
    string? SeriesPosition,
    IReadOnlyList<string> AlsoByTitles,
    IReadOnlyList<SeriesListing> SeriesCatalog)
{
    public static readonly ContentDetermination Empty =
        new(null, null, null, null, null, Array.Empty<string>(), Array.Empty<SeriesListing>());

    public bool HasAnything =>
        Isbn is not null || Title is not null || Author is not null
        || Series is not null || AlsoByTitles.Count > 0 || SeriesCatalog.Count > 0;
}

// Pure heuristic parser: given the leading text of a book, guesses ISBN, title,
// author, series and any "also by this author" list found in the front matter.
// Network-free and deterministic so it unit-tests cleanly; BookTextReader is
// what turns a file into the text fed in here.
public static class FrontMatterExtractor
{
    private static readonly Regex IsbnLabeled = new(
        @"ISBN(?:[-\s]?1[03])?\s*:?\s*([0-9][0-9\-\s]{8,16}[0-9Xx])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Isbn13Bare = new(
        @"\b(97[89][-\s]?(?:[0-9][-\s]?){9}[0-9])\b", RegexOptions.Compiled);

    // Project Gutenberg / e-reader plain-text headers.
    private static readonly Regex GutenbergTitle = new(@"^\s*Title\s*:\s*(.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GutenbergAuthor = new(@"^\s*Author\s*:\s*(.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "Copyright © 1999 by Anne McCaffrey" / "© 1999 Anne McCaffrey".
    private static readonly Regex CopyrightBy = new(
        @"(?:copyright|©|\(c\))[^\n]*?\bby\s+(" + NamePattern + @")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CopyrightYearName = new(
        @"©\s*\d{4}\s+(" + NamePattern + @")", RegexOptions.Compiled);

    // Headings introducing a same-author bibliography. Captures the author name
    // when the heading carries one ("Novels by G R Jordan").
    private static readonly Regex AlsoByHeading = new(
        @"^\s*(?:also\s+by|other\s+(?:books|titles|novels)\s+by|books\s+by|novels\s+by|titles\s+by|by\s+the\s+same\s+author)\b\s*:?\s*(" + NamePattern + @")?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A line that names a series within a bibliography, e.g.
    //   "The Highlands and Islands Detective series (Crime)"
    //   "Kirsten Stewart Thrillers (Thriller)"
    // Captures the series name and the optional parenthetical genre.
    private static readonly Regex SeriesHeaderWithGenre = new(
        @"^(?<name>.+?)\s*\((?<genre>[^)]{2,40})\)\s*$", RegexOptions.Compiled);
    // A series header with no genre, identified by a trailing collection word.
    private static readonly Regex SeriesHeaderKeyword = new(
        @"\b(?:series|saga|trilogy|quartet|quintet|sextet|cycle|chronicles|mysteries|thrillers|novels|stories|sequence|duology|omnibus)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // A trailing generic " series" descriptor we drop from the stored name
    // (so "Detective series" -> "Detective"); kept words like "Mysteries" stay.
    private static readonly Regex TrailingSeriesWord = new(@"\s+series\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "Book Three of the Pern Chronicles" / "Book 3 of the X Series". The name
    // capture excludes sentence punctuation so a prose line ("…is book 1 in the
    // Royal Alphas series. It is its own self-contained story…") can't smear a
    // whole paragraph into the series name — seen in live data.
    private static readonly Regex SeriesLine = new(
        @"\bbook\s+(\w+)\s+(?:of|in)\s+(?:the\s+)?([^.!?]+?)(?:\s+(?:series|saga|trilogy|cycle|chronicles))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A capitalised personal name of 1–4 words. Each word starts uppercase and
    // may be a bare initial ("G", "R") or a full word ("Jordan", "O'Brien"), so
    // initial-style by-lines like "G R Jordan" are captured.
    private const string NamePattern = @"[\p{Lu}][\p{L}.'\-]*(?:\s+[\p{Lu}][\p{L}.'\-]*){0,3}";

    private static readonly Dictionary<string, int> Ordinals = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6,
        ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["first"] = 1, ["second"] = 2,
        ["third"] = 3, ["fourth"] = 4, ["fifth"] = 5, ["sixth"] = 6, ["seventh"] = 7,
        ["eighth"] = 8, ["ninth"] = 9, ["tenth"] = 10,
    };

    public static ContentDetermination Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ContentDetermination.Empty;

        var lines = text.Replace("\r", "")
            .Split('\n')
            .Select(l => l.Trim())
            .ToList();
        var joined = string.Join("\n", lines);

        var isbn = FindIsbn(joined);
        string? title = null, author = null, series = null, seriesPos = null;
        var alsoBy = new List<string>();
        var catalog = new List<SeriesListing>();

        // Project Gutenberg / sidecar headers — highest-confidence title+author.
        foreach (var l in lines.Take(60))
        {
            if (title is null && GutenbergTitle.Match(l) is { Success: true } tm && LooksLikeTitle(tm.Groups[1].Value))
                title = Clean(tm.Groups[1].Value);
            if (author is null && GutenbergAuthor.Match(l) is { Success: true } am)
                author = CleanAuthor(am.Groups[1].Value);
        }

        // Copyright line → author. Matched per line so the name can't run past
        // the line end into the next line's text.
        if (author is null)
        {
            foreach (var l in lines.Take(120))
            {
                if (CopyrightBy.Match(l) is { Success: true } cm
                    && CleanAuthor(cm.Groups[1].Value) is { } a1) { author = a1; break; }
                if (CopyrightYearName.Match(l) is { Success: true } cy
                    && CleanAuthor(cy.Groups[1].Value) is { } a2) { author = a2; break; }
            }
        }

        // Series line. Real series names are short — a long capture means the
        // regex latched onto running prose, not a "Book N of the X" line.
        foreach (var l in lines.Take(80))
        {
            var sm = SeriesLine.Match(l);
            if (sm.Success && sm.Groups[2].Value.Trim().Length <= 60 && LooksLikeTitle(sm.Groups[2].Value))
            {
                series = Clean(sm.Groups[2].Value);
                var token = sm.Groups[1].Value;
                seriesPos = int.TryParse(token, out var n) ? n.ToString()
                    : Ordinals.TryGetValue(token, out var o) ? o.ToString() : null;
                break;
            }
        }

        // "Also by / Novels by <Author>" — capture the author and parse the
        // bibliography that follows into series-grouped titles. Many authors lay
        // this out as a series header (sometimes "(Genre)"), a blank line, then
        // the titles in that series, with blank lines between series — so we must
        // read *through* blank lines rather than stop at the first one. We scan
        // EVERY such heading (front and back matter both carry these lists) and
        // merge the results, since head+tail text is fed in together.
        var bi = 0;
        while (bi < lines.Count)
        {
            var hm = AlsoByHeading.Match(lines[bi]);
            if (hm.Success)
            {
                if (author is null && hm.Groups[1].Success) author = CleanAuthor(hm.Groups[1].Value);
                bi = ParseBibliography(lines, bi + 1, alsoBy, catalog);
            }
            else bi++;
        }
        MergeDuplicateSeries(catalog);
        DedupeInPlace(alsoBy);

        // Title page fallback: first plausible "real" line, optionally a "by X".
        if (title is null || author is null)
        {
            var (tpTitle, tpAuthor) = TitlePageGuess(lines);
            title ??= tpTitle;
            author ??= tpAuthor;
        }

        return new ContentDetermination(isbn, title, author, series, seriesPos, alsoBy, catalog);
    }

    // Reads the body of a bibliography starting at startIndex, filling both the
    // flat title list (every title, in order) and the series catalogue (titles
    // grouped under the series header they appear beneath). Titles that appear
    // before any series header are ungrouped — they go into the flat list only.
    // Returns the line index where parsing stopped, so the caller can resume the
    // search for further "Also by" headings after this block.
    private static int ParseBibliography(
        IReadOnlyList<string> lines, int startIndex, List<string> flat, List<SeriesListing> catalog)
    {
        string? curSeries = null, curGenre = null;
        var curTitles = new List<string>();

        void Flush()
        {
            if (curSeries is not null && curTitles.Count > 0)
                catalog.Add(new SeriesListing(curSeries, curGenre, curTitles.ToList()));
            curTitles.Clear();
        }

        // Bound the scan so a missing terminator can't swallow the whole book.
        var end = Math.Min(lines.Count, startIndex + 250);
        var j = startIndex;
        for (; j < end; j++)
        {
            var line = lines[j];
            if (line.Length == 0) continue;               // blank: separates blocks, keep reading
            if (IsHeadingish(line)) break;                // Contents / Chapter / etc. ends it
            if (AlsoByHeading.IsMatch(line)) break;       // another "Also by" — let the caller restart there
            if (flat.Count >= 300) break;                 // sanity cap

            if (TryReadSeriesHeader(line, out var name, out var genre))
            {
                Flush();
                curSeries = name;
                curGenre = genre;
                continue;
            }

            // Otherwise it's a title — unless it reads like prose, which means the
            // bibliography is over and the book proper has begun. Judge the
            // *cleaned* form (so a trailing-period title like "Antisocial
            // Behaviour." is kept) and require it to be title-cased, which stops
            // dedications / warnings / epigraphs from being slurped in as titles
            // — even in hard-wrapped .txt where every prose line is short.
            var cleaned = CleanListItem(line);
            if (!IsListedTitle(cleaned)) break;
            if (cleaned.Length < 2) continue;
            flat.Add(cleaned);
            if (curSeries is not null) curTitles.Add(cleaned);
        }
        Flush();
        return j;
    }

    // Merges catalogue entries naming the same series (case-insensitive) into one,
    // unioning their titles in first-seen order and keeping the first genre found.
    // Front- and back-matter copies of the same list therefore collapse together.
    private static void MergeDuplicateSeries(List<SeriesListing> catalog)
    {
        if (catalog.Count < 2) return;
        var order = new List<string>();
        var byKey = new Dictionary<string, (string Series, string? Genre, List<string> Titles)>(StringComparer.OrdinalIgnoreCase);
        foreach (var listing in catalog)
        {
            var key = listing.Series.Trim().ToLowerInvariant();
            if (!byKey.TryGetValue(key, out var acc))
            {
                acc = (listing.Series, listing.Genre, new List<string>());
                byKey[key] = acc;
                order.Add(key);
            }
            else if (acc.Genre is null && listing.Genre is not null)
            {
                acc = (acc.Series, listing.Genre, acc.Titles);
                byKey[key] = acc;
            }
            var seen = new HashSet<string>(acc.Titles, StringComparer.OrdinalIgnoreCase);
            foreach (var t in listing.Titles) if (seen.Add(t)) acc.Titles.Add(t);
        }
        catalog.Clear();
        foreach (var key in order)
        {
            var acc = byKey[key];
            catalog.Add(new SeriesListing(acc.Series, acc.Genre, acc.Titles));
        }
    }

    // Removes case-insensitive duplicate titles, preserving first-seen order.
    private static void DedupeInPlace(List<string> items)
    {
        if (items.Count < 2) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(items.Count);
        foreach (var s in items) if (seen.Add(s)) result.Add(s);
        items.Clear();
        items.AddRange(result);
    }

    // True when a bibliography line names a series (rather than a title). A
    // parenthetical genre is the strongest signal; otherwise a trailing
    // collection word ("Chronicles", "Thrillers", "saga", ...).
    private static bool TryReadSeriesHeader(string line, out string name, out string? genre)
    {
        name = ""; genre = null;
        var gm = SeriesHeaderWithGenre.Match(line);
        if (gm.Success && IsPlausibleTitle(gm.Groups["name"].Value))
        {
            name = CleanSeriesName(gm.Groups["name"].Value);
            genre = Clean(gm.Groups["genre"].Value);
            return name.Length >= 2;
        }
        if (SeriesHeaderKeyword.IsMatch(line) && IsPlausibleTitle(line))
        {
            name = CleanSeriesName(line);
            return name.Length >= 2;
        }
        return false;
    }

    private static string CleanSeriesName(string s) => Clean(TrailingSeriesWord.Replace(s.Trim(), ""));

    // A short, non-prose line that could be a book title or series name.
    private static bool IsPlausibleTitle(string s)
    {
        s = s.Trim();
        if (s.Length is < 2 or > 90) return false;        // prose paragraphs are long
        if (!s.Any(char.IsLetter)) return false;
        if (s.EndsWith('.') || s.EndsWith(':')) return false; // sentence / label, not a title
        if (IsSeriesDescriptor(s)) return false;          // "Book 2 of the X Series" is position, not a title
        return !IsHeadingish(s);
    }

    // "Book 2 of the Sword Dancer Series" / "Book Three in the Pern Chronicles"
    // and the like name a series + position, NOT a title — they feed Series /
    // SeriesPosition. Letting one become the guessed title is how unmatchable
    // titles like "Book 2 Of The Sword Dancer Series" reached OpenLibrary search.
    private static bool IsSeriesDescriptor(string s) => SeriesLine.IsMatch(s.Trim());

    private static string? FindIsbn(string text)
    {
        foreach (Match m in IsbnLabeled.Matches(text))
            if (EpubMetadataReader.NormaliseIsbn(m.Groups[1].Value) is { } n) return n;
        var bare = Isbn13Bare.Match(text);
        return bare.Success ? EpubMetadataReader.NormaliseIsbn(bare.Groups[1].Value) : null;
    }

    // "Walter M. Miller, Jr." — a generational suffix after the name. Without
    // this the whole-line byline regex fails on the comma and a textbook title
    // page yields nothing (seen in live data).
    private const string NameSuffixPattern = @"(?:\s*,\s*(?:Jr|Sr|II|III|IV)\.?)?";

    // Looks at the very top of the book for a title page: "<Title>" then "by
    // <Author>", or both on one line ("The Star by Arthur C. Clarke").
    // Deliberately conservative — the byline must be near the top and the title
    // must sit just above it (or before "by" on the same line) — so a stray
    // "by X" attribution (an epigraph, a dedication, "foreword by …") deep in
    // the front matter can't promote a random line of text into "the title".
    private static (string? Title, string? Author) TitlePageGuess(IReadOnlyList<string> lines)
    {
        var head = lines.Where(l => l.Length > 0).Take(12).ToList();
        for (var i = 0; i < head.Count; i++)
        {
            string? author = null;

            // Form A: "by <Author>" on one line.
            var inline = Regex.Match(head[i], @"^\s*by\s+(" + NamePattern + NameSuffixPattern + @")\s*$", RegexOptions.IgnoreCase);
            if (inline.Success)
                author = CleanAuthor(inline.Groups[1].Value);
            // Form B: a standalone "by" connector line — the title sits above it and
            // the author on a following line ("Title" / "by" / "Manley Wade Wellman").
            // This is the most common ebook title-page layout. (Blank lines are
            // already filtered out of `head`, so the author is within a line or two.)
            else if (Regex.IsMatch(head[i], @"^\s*by\s*$", RegexOptions.IgnoreCase))
            {
                for (var k = i + 1; k < head.Count && k <= i + 3; k++)
                {
                    var nm = Regex.Match(head[k], @"^\s*(" + NamePattern + NameSuffixPattern + @")\s*$");
                    if (nm.Success && CleanAuthor(nm.Groups[1].Value) is { } a) { author = a; break; }
                }
            }

            if (author is null) continue;
            // The title must be a plausible line within the 3 lines directly above
            // the byline — not just any earlier line.
            string? title = null;
            for (var k = i - 1; k >= 0 && k >= i - 3; k--)
                if (IsPlausibleTitle(head[k])) { title = Clean(head[k]); break; }
            return (title, author);
        }

        // Single-line "<Title> by <Author>" form. The title part must read like a
        // listed title (title-cased, short) so a prose sentence containing "by"
        // ("…handiwork made by God in the heavens…") can't match.
        foreach (var l in head)
        {
            // "by" matched case-explicitly: IgnoreCase here would also relax the
            // uppercase-first requirement inside NamePattern and let prose through.
            var m = Regex.Match(l, @"^(?<t>.{2,90}?)\s+(?:by|By|BY)\s+(?<a>" + NamePattern + NameSuffixPattern + @")\s*$");
            if (!m.Success) continue;
            var titlePart = m.Groups["t"].Value.Trim();
            if (!IsListedTitle(CleanListItem(titlePart))) continue;
            return (Clean(titlePart), CleanAuthor(m.Groups["a"].Value));
        }
        return (null, null);
    }

    private static bool LooksLikeTitle(string s)
    {
        s = s.Trim();
        if (s.Length < 2 || s.Length > 200) return false;
        if (!s.Any(char.IsLetter)) return false;
        if (IsSeriesDescriptor(s)) return false; // series + position, not a title
        // Reject obvious boilerplate.
        var low = s.ToLowerInvariant();
        return !(low.Contains("copyright") || low.Contains("all rights reserved")
            || low.StartsWith("isbn") || low.StartsWith("published") || low.StartsWith("www.")
            || low.Contains("press") && low.Length < 30);
    }

    private static bool IsHeadingish(string s)
    {
        var low = s.ToLowerInvariant();
        return low is "contents" or "table of contents" or "chapter one" or "prologue" or "epilogue"
            or "introduction" or "dedication" or "warning" or "preface" or "foreword" or "afterword"
            or "epigraph" or "acknowledgements" or "acknowledgments" or "author's note" or "a note"
            or "note" or "about the author" or "about the book" or "praise" or "title page"
            || low.StartsWith("chapter ") || low.StartsWith("part ") || low.StartsWith("copyright")
            || low.StartsWith("praise for ") || low.StartsWith("about ");
    }

    // Minor words that may stay lowercase in a real title — excluded from the
    // title-case test so "The Lord of the Rings" still reads as title case.
    private static readonly HashSet<string> TitleMinorWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "the", "of", "or", "nor", "to", "in", "on", "at", "by", "for",
        "with", "from", "as", "but", "vs", "per", "via", "into", "onto",
    };

    // A bibliography list entry is a *title*, not a line of prose. The decisive
    // signal (robust even for hard-wrapped .txt where every prose line is short)
    // is title case: a real title capitalises its significant words, while prose
    // ("This book, like The Gilded Chain, is a") is mostly lowercase. Requires the
    // significant (non-minor) words to be ≥ 70% capitalised/numeric.
    private static bool IsListedTitle(string s)
    {
        if (!IsPlausibleTitle(s)) return false;
        // A title always capitalises its first word; a wrapped prose line often
        // begins lowercase ("from Avon Books", "stand-alone novel…").
        var first = s.FirstOrDefault(char.IsLetterOrDigit);
        if (first != default && char.IsLower(first)) return false;
        var words = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int significant = 0, capitalised = 0;
        foreach (var w in words)
        {
            var word = w.Trim('"', '\'', '(', ')', '“', '”', '‘', '’', '.', ',', '—', '-');
            if (word.Length == 0 || !word.Any(char.IsLetter)) continue;
            if (TitleMinorWords.Contains(word)) continue; // lowercase connectors don't count
            significant++;
            if (char.IsUpper(word[0]) || char.IsDigit(word[0])) capitalised++;
        }
        if (significant == 0) return false;
        return capitalised >= significant * 0.7;
    }

    private static string CleanListItem(string s)
    {
        // Strip leading bullets and explicit list numbering ("3. ", "12) ") only.
        // Bare digits stay — they're part of real titles ("1984", "3:10 to Yuma"),
        // which the old any-leading-digit strip used to mangle or drop entirely.
        s = Regex.Replace(s, @"^\s*(?:[•\-\*]+\s*)*(?:\d{1,3}\s*[.)]\s+)?", "");
        s = Clean(s);
        // A single trailing sentence period isn't part of a title ("Antisocial
        // Behaviour." → "Antisocial Behaviour"). Keep "!"/"?" — they occur in real
        // titles ("Man Overboard!"). Don't strip "..." either.
        if (s.EndsWith('.') && !s.EndsWith("..", StringComparison.Ordinal))
            s = s[..^1].TrimEnd();
        return s;
    }

    private static string Clean(string s)
    {
        s = Regex.Replace(s.Trim(), @"\s+", " ");
        return s.Length > 480 ? s[..480].TrimEnd() : s;
    }

    // Copyright-notice words that the capitalised-name regex slurps into an author
    // capture from a same-line notice ("by John Smith. All rights reserved" →
    // "John Smith. All"). They never appear inside a real author name, so the name
    // ends as soon as one shows up.
    private static readonly HashSet<string> AuthorBoilerplate = new(StringComparer.OrdinalIgnoreCase)
    {
        "all", "rights", "reserved", "copyright", "published", "publishing", "ebook", "edition",
    };

    // An "author" ending in one of these is a publisher / imprint pulled from the
    // copyright page ("© 2021 Blue Fire Media"), not a person — refuse the whole
    // capture rather than ship a name OpenLibrary can never resolve (live data:
    // "Blue Fire Media", "ebook Carousel").
    private static readonly HashSet<string> PublisherTailWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "media", "press", "publishing", "publications", "publishers", "books",
        "inc", "inc.", "llc", "ltd", "ltd.", "limited", "group", "studio", "studios",
    };

    // Splits a multi-author capture ("Frederik Pohl and Thomas T. Thomas",
    // "Scott Nicholson; J. R. Rain") down to the FIRST author. Only one author can
    // be resolved/assigned anyway, and the 4-word name cap otherwise truncates the
    // capture mid-name ("Frederik Pohl and Thomas" — live data), which resolves to
    // nobody.
    private static readonly Regex AuthorListSeparator = new(
        @"\s*(?:;|&|\s+(?:and|AND|And|with)\s+)\s*", RegexOptions.Compiled);

    // A pure-initials token like "J." or "J.R.R." — its trailing period is part of
    // the name, not a sentence end, so it must NOT terminate the author.
    private static readonly Regex InitialsToken = new(@"^(?:\p{Lu}\.)+$", RegexOptions.Compiled);

    // Cleans a captured author name, dropping copyright-notice boilerplate the name
    // regex can run into. The name ends at the first boilerplate word OR at the
    // sentence-ending period after a real word ("Smith." → "Smith", stop), while
    // genuine initials ("J.R.R. Tolkien") are kept intact. Returns null when nothing
    // usable remains.
    private static string? CleanAuthor(string raw)
    {
        var cleaned = Clean(raw);
        if (string.IsNullOrWhiteSpace(cleaned)) return null;

        // Multi-author credit → keep the first author whole.
        cleaned = AuthorListSeparator.Split(cleaned)[0].Trim();
        if (cleaned.Length == 0) return null;

        var kept = new List<string>();
        foreach (var w in cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (AuthorBoilerplate.Contains(w.Trim('.', ','))) break;
            if (w.EndsWith('.') && !InitialsToken.IsMatch(w)) { kept.Add(w.TrimEnd('.')); break; }
            kept.Add(w);
        }
        var result = string.Join(' ', kept).Trim().TrimEnd(',');
        if (result.Length == 0) return null;
        // URL fragments and publishers aren't authors.
        var low = result.ToLowerInvariant();
        if (low.Contains("http") || low.Contains("www.") || low.Contains(".com") || result.Contains('_'))
            return null;
        var lastWord = result.Split(' ')[^1];
        if (kept.Count > 1 && PublisherTailWords.Contains(lastWord)) return null;
        return result;
    }
}

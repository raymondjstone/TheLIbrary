using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TheLibrary.Server.Services.Sync;

public static class TitleNormalizer
{
    // Matches the trailing "series number" in the first " - " segment of a filename,
    // e.g. "Chaoswar Saga 03" → series="Chaoswar Saga", position="3".
    // Requires at least one non-digit word before the number so bare numbers like
    // "1984" don't accidentally match. Also accepts "Book"/"Vol"/"Volume"/"Part"
    // keywords in front of the number, e.g. "Hayley Powell Book 3".
    private static readonly Regex SeriesSegmentRx = new(
        @"^(.+?)\s+(?:book\s+|vol(?:ume)?\s*\.?\s*|part\s+)?#?(\d+(?:\.\d+)?)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Matches a bracket-wrapped series prefix at the start of a segment,
    // e.g. "[Lorien Legacies 06.0] The Fate" → series="Lorien Legacies",
    // pos="6", remainder="The Fate". Supports either '[]' or '()' delimiters
    // and an optional "#" prefix on the number.
    private static readonly Regex BracketedSeriesPrefixRx = new(
        @"^[\[(]\s*(.+?)\s+(?:book\s+|vol(?:ume)?\s*\.?\s*|part\s+)?#?(\d+(?:\.\d+)?)\s*[\])]\s*(.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Matches a bare number segment like "069", "3", "1.5", "#7". Used to detect
    // patterns where the position lives in its own " - " segment, e.g.
    // "Star Trek_ TNG - 069 - Insurrection".
    private static readonly Regex BareNumberSegmentRx = new(
        @"^#?(\d+(?:\.\d+)?)$", RegexOptions.Compiled);

    // Tool-added duplicate suffix on the bare title like "_2", "_3" — Calibre
    // and a few download tools use this to disambiguate library-side conflicts.
    private static readonly Regex TrailingDupSuffixRx = new(
        @"_\d+$", RegexOptions.Compiled);

    // Trims a trailing "Book"/"Volume"/"Vol"/"Part" keyword that
    // SeriesSegmentRx captures into the series name when the keyword is
    // comma-attached, e.g. "Hank_ Texas Kings MC, Book 11" → "Hank_ Texas
    // Kings MC". Comma + keyword at the end is always noise.
    private static readonly Regex TrailingKeywordRx = new(
        @"[,\s]+(?:book|vol(?:ume)?|part)\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Calibre appends "(123)" — strip the trailing id in parens.
    private static readonly Regex TrailingIdParens = new(@"\s*\(\d+\)\s*$", RegexOptions.Compiled);

    // Format/version tags embedded in titles — e.g. "(v1.0)", "[rtf]", "[epub]",
    // "(retail)" — left behind when a filename carries multiple metadata tokens.
    // Supports both () and [] delimiters; stripped wherever they appear.
    private static readonly Regex TitleFormatTag = new(
        @"\s*[\(\[](?:mobi|epub|pdf|azw3?|lit|prelit|rtf|txt|html?|fb2|docx|retail|scan|ocr|v\d+(?:\.\d+)*)[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NonAlnum = new(@"[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly string[] LeadingArticles = { "the ", "a ", "an " };

    // Strips any trailing parenthetical group from a raw folder name,
    // e.g. "Title (Author Name)" → "Title".
    private static readonly Regex TrailingParensGroup = new(@"\s*\([^)]*\)\s*$", RegexOptions.Compiled);

    // After normalization (lowercase, spaces only) matches the LAST " by <2+ word author>"
    // suffix. Greedy (.+) ensures we take the longest possible title portion.
    // Requires ≥2 words after "by" so "Stand By Me" is never truncated.
    private static readonly Regex ByAuthorSuffixNorm = new(@"^(.+)\s+by\s+\w+(?:\s+\w+)+\s*$", RegexOptions.Compiled);

    // Cap below SQL Server's 1700-byte nonclustered-index-key limit: with a
    // 4-byte AuthorId also in IX_*_AuthorId_NormalizedTitle, nvarchar values
    // above ~848 chars bust the index. Overly long OL titles (compilations
    // with every included work listed in the title) have been seen at 900+
    // chars; 800 is comfortably under the limit and preserves enough signal
    // to keep match accuracy on normal-length titles.
    public const int MaxNormalizedLength = 800;

    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var s = TrailingIdParens.Replace(input, "").Trim();
        s = StripDiacritics(s).ToLowerInvariant();
        foreach (var art in LeadingArticles)
            if (s.StartsWith(art)) { s = s[art.Length..]; break; }
        s = NonAlnum.Replace(s, " ").Trim();
        s = Regex.Replace(s, @"\s+", " ");
        if (s.Length > MaxNormalizedLength) s = s[..MaxNormalizedLength].TrimEnd();
        return s;
    }

    // Returns normalized title candidates for a Calibre folder name, from most
    // to least specific. The matching loop in SyncService stops at the first hit.
    //
    // Handled patterns (in addition to straight normalization):
    //   "Title (Author Name)"     — trailing parenthetical stripped
    //   "Title by Author Name"    — " by <2+ word name>" suffix stripped
    //   "Title_by_Author"         — underscores/dashes become spaces → same rule
    //   "Title (Author) by Name"  — parens stripped first, then by-author
    public static IEnumerable<string> FolderTitleCandidates(string folder)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // 1. Straight normalization — highest confidence, tried first.
        var n1 = Normalize(folder);
        if (n1.Length > 0 && seen.Add(n1)) yield return n1;

        // 2. Strip trailing "(...)" from the raw folder name, then normalize.
        var noParens = TrailingParensGroup.Replace(folder, "").Trim();
        string? n2 = null;
        if (noParens.Length > 0 && noParens != folder)
        {
            n2 = Normalize(noParens);
            if (n2.Length > 0 && seen.Add(n2)) yield return n2;
        }

        // 3. Strip " by <FirstName LastName>" suffix from the already-normalized text.
        var m1 = ByAuthorSuffixNorm.Match(n1);
        if (m1.Success)
        {
            var n3 = m1.Groups[1].Value.Trim();
            if (n3.Length > 0 && seen.Add(n3)) yield return n3;

            // 4. Parens stripped + by-author stripped.
            if (n2 is not null)
            {
                var m2 = ByAuthorSuffixNorm.Match(n2);
                if (m2.Success)
                {
                    var n4 = m2.Groups[1].Value.Trim();
                    if (n4.Length > 0 && seen.Add(n4)) yield return n4;
                }
            }
        }
    }

    // A plausible author folder name must pass three gates:
    //   1. Starts with a letter — rejects bracket prefixes ([美]...), punctuation
    //      prefixes (. Array, . . . Brock, 'Nathan), digit-prefixed names (2.16 ...).
    //   2. At least four letter characters total — rejects single-char names, two-char
    //      abbreviations (DS, PP, hw), and three-letter acronyms (ABS, CSS, LU).
    //   3. At least one uppercase letter — every legitimate author name capitalises at
    //      least one word; rejects all-lowercase metadata garbage (lasg, faq, rolo,
    //      moon) and multi-word lowercase strings ("for my mother", "thank you").
    public static bool IsPlausibleAuthorName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var s = name.Trim();
        if (!char.IsLetter(s[0])) return false;
        var letters = s.Where(char.IsLetter).ToArray();
        if (letters.Length < 4) return false;
        if (!letters.Any(char.IsUpper)) return false;
        return true;
    }

    // Parses a filename stem following any of the recognised series naming
    // conventions. Returns (null,null,null,null) when no series anchor can be
    // identified. Patterns handled (illustrative — order independent):
    //
    //   "{Series} [#]N - {Title}"                       e.g. "Heechee 6 - Title"
    //   "{Series} [#]N - {Title} - {Author}"            e.g. "Heechee 6 - Title - Pohl"
    //   "{Author} - {Series} [#]N - {Title}[...]"       e.g. "Pohl, F - Heechee 6 - Title"
    //   "{Series} - NN - {Title}[ - {Author}]"          e.g. "Star Trek_ TNG - 069 - Show"
    //   "[{Series} N] {Title} - {Author}"               e.g. "[Lorien Legacies 06] Fate - Pittacus"
    //   "{Author} - [{Series} N] - {Title}"             e.g. "Marta Perry - [Watcher 05] - Strike"
    //   "{Series}, Book N - {Title}[ - {Author}]"       e.g. "Hank, Book 11 - Cee"
    //   "{Series Vol. N} - {Title}"                     e.g. "Holmes of Kyoto Vol. 6"
    //
    // Author, when present, is in either "Last, First" sort format or display
    // form. Trailing _ . spaces on the author and trailing _N / (123) Calibre
    // suffixes on the title are stripped.
    public static (string? Series, string? Position, string? Title, string? Author) TryParseSeriesFilename(string? stem)
    {
        if (string.IsNullOrWhiteSpace(stem)) return (null, null, null, null);

        var parts = stem.Split(" - ", StringSplitOptions.None);
        for (int i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim();
        if (parts.Length < 2) return (null, null, null, null);

        string? series = null;
        string? pos = null;
        int anchorStart = -1, anchorEnd = -1;
        string titlePrefix = "";

        // 1) Single-segment anchor — bracketed prefix or "Series N" suffix.
        //    Scan forward so the earliest plausible series wins; if the user has
        //    nested "Universe - 311 - Subseries 03 - Title", the bare "311" segment
        //    won't match here and the inner "Subseries 03" hit is detected later.
        for (int i = 0; i < parts.Length; i++)
        {
            // Bracket-prefix in the middle of a segment: "[Series N] Title…".
            var pre = BracketedSeriesPrefixRx.Match(parts[i]);
            if (pre.Success && !string.IsNullOrWhiteSpace(pre.Groups[1].Value))
            {
                series = pre.Groups[1].Value.Trim();
                pos = NormalisePosition(pre.Groups[2].Value);
                titlePrefix = pre.Groups[3].Value.Trim();
                anchorStart = i; anchorEnd = i;
                break;
            }

            // "Series N" with optional bracket wrappers — strip brackets first so
            // "[Series N]" and "(Series N)" both match the same regex.
            var stripped = StripBrackets(parts[i]);
            var m = SeriesSegmentRx.Match(stripped);
            if (m.Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value))
            {
                series = m.Groups[1].Value.Trim();
                pos = NormalisePosition(m.Groups[2].Value);
                anchorStart = i; anchorEnd = i;
                break;
            }
        }

        // 2) Two-segment anchor — "{Series} - NN - …" where position is alone.
        if (series is null)
        {
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var bm = BareNumberSegmentRx.Match(parts[i + 1]);
                if (!bm.Success) continue;
                var seriesCand = StripBrackets(parts[i]);
                if (!LooksLikeSeriesName(seriesCand)) continue;
                series = seriesCand;
                pos = NormalisePosition(bm.Groups[1].Value);
                anchorStart = i; anchorEnd = i + 1;
                break;
            }
        }

        if (series is null) return (null, null, null, null);

        // Trim a trailing ", Book"/", Vol"/", Volume"/", Part" keyword that the
        // SeriesSegmentRx leaves behind when the keyword is comma-attached, e.g.
        // "Hank_ Texas Kings MC, Book" → "Hank_ Texas Kings MC".
        series = TrimTrailingSeriesKeyword(series);

        // Build the list of "effective title pieces": the in-segment prefix (from
        // a bracketed series header) plus every segment after the anchor. This
        // collapses the bracketed-prefix and trailing-segments cases into one
        // shared author-detection rule.
        string? author = null;
        var titlePieces = new List<string>();
        if (!string.IsNullOrEmpty(titlePrefix)) titlePieces.Add(titlePrefix);
        for (int j = anchorEnd + 1; j < parts.Length; j++)
            if (!string.IsNullOrEmpty(parts[j])) titlePieces.Add(parts[j]);

        if (anchorStart > 0)
        {
            // Author lives in the segment immediately before the anchor — unless
            // it is a bare number (parent-series index), which we drop entirely.
            var maybeAuthor = parts[anchorStart - 1].TrimEnd('_', ' ', '.');
            if (LooksLikeAuthor(maybeAuthor)) author = maybeAuthor;
        }
        else if (titlePieces.Count >= 2)
        {
            // Anchor at parts[0] and ≥2 effective title pieces: the LAST piece is
            // treated as the author (legacy "Series N - Title - Author").
            author = titlePieces[^1].TrimEnd('_', ' ', '.');
            titlePieces.RemoveAt(titlePieces.Count - 1);
        }

        var title = string.Join(" - ", titlePieces);
        title = TitleFormatTag.Replace(title, "").Trim();
        title = TrailingIdParens.Replace(title, "").Trim();
        title = TrailingDupSuffixRx.Replace(title, "").Trim();

        return (series,
                pos,
                string.IsNullOrWhiteSpace(title) ? null : title,
                string.IsNullOrWhiteSpace(author) ? null : author);
    }

    // Strips a single layer of surrounding [], (), or {} from a segment. Used so
    // bracket-wrapped series segments like "[Series N]" match the same regex as
    // bare "Series N".
    private static string StripBrackets(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        s = s.Trim();
        if (s.Length < 2) return s;
        var first = s[0];
        var last = s[^1];
        if ((first == '[' && last == ']') ||
            (first == '(' && last == ')') ||
            (first == '{' && last == '}'))
            return s[1..^1].Trim();
        return s;
    }

    private static string TrimTrailingSeriesKeyword(string s) =>
        TrailingKeywordRx.Replace(s, "").TrimEnd(',', ' ').Trim();

    // A series name must have a couple of letter characters and must not be a
    // pure number; rejects "311", "008" as parent-series prefix noise.
    private static bool LooksLikeSeriesName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        return s.Count(char.IsLetter) >= 2;
    }

    // A plausible author name has at least 2 letters and isn't a bare number.
    // We don't try harder than that — when the position lives in its own segment
    // and the preceding segment is a parent-series index like "311", we want to
    // drop it; everything else (including ambiguous "Star Wars"-like prefixes)
    // is accepted to avoid losing real authors.
    private static bool LooksLikeAuthor(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (BareNumberSegmentRx.IsMatch(s)) return false;
        return s.Count(char.IsLetter) >= 2;
    }

    // Normalises "03" → "3", "3.0" → "3".
    private static string NormalisePosition(string raw)
    {
        var s = raw.TrimStart('0');
        if (string.IsNullOrEmpty(s)) s = "0";
        if (s.EndsWith(".0", StringComparison.Ordinal)) s = s[..^2];
        return s;
    }

    // Strips editor credits without parens from author names, e.g.
    // "Bradley, Marion Zimmer Ed." → "Bradley, Marion Zimmer".
    // Applied before normalization so the suffix doesn't contaminate the key.
    private static readonly Regex EditorSuffixRx = new(
        @"\s+Ed\.?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string NormalizeAuthor(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var s = StripDiacritics(input).ToLowerInvariant();
        // Strip bare " Ed." / " Ed " editor suffix before comma-flip so
        // "Bradley, Marion Zimmer Ed." → "marion zimmer bradley", not "marion zimmer ed bradley".
        s = Regex.Replace(s, @"\s+ed\.?\s*$", "", RegexOptions.IgnoreCase);
        // Calibre sometimes writes "Last, First" — flip it.
        var comma = s.IndexOf(',');
        if (comma > 0)
            s = (s[(comma + 1)..].Trim() + " " + s[..comma].Trim()).Trim();
        s = NonAlnum.Replace(s, " ").Trim();
        return Regex.Replace(s, @"\s+", " ");
    }

    private static string StripDiacritics(string input)
    {
        var norm = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(norm.Length);
        foreach (var ch in norm)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            // Letters whose "accent" is part of the glyph (stroke, slash,
            // ligature) don't decompose in FormD — fold them by hand so
            // "Stanisław" matches a plain-ASCII "Stanislaw". NOTE: stored
            // NormalizedName values (OL catalogue, blacklist) only pick this
            // up when they are next rebuilt (the OL seed job re-normalizes).
            sb.Append(ch switch
            {
                'ł' => "l", 'Ł' => "L", 'ø' => "o", 'Ø' => "O",
                'đ' => "d", 'Đ' => "D", 'ð' => "d", 'Ð' => "D",
                'þ' => "th", 'Þ' => "Th", 'ß' => "ss",
                'æ' => "ae", 'Æ' => "Ae", 'œ' => "oe", 'Œ' => "Oe",
                'ı' => "i", 'ŀ' => "l", 'Ŀ' => "L",
                _ => ch.ToString(),
            });
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}

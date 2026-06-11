using System.Text.RegularExpressions;

namespace TheLibrary.Server.Services.Sync;

public sealed record FilenameGuess(string? Author, string? Title, string? Series, string? SeriesPosition);

// Pure heuristic parser for the FILENAME of an untracked book. Live data shows
// the quarantine folder is full of files whose content yields nothing (DRM'd
// AZW3s, prose-from-line-one .txt) but whose name plainly carries the answer:
//   "Murder First Class - Leonard Gribble.azw3"        (Title - Author)
//   "A. N. Pearce - The Google Questions.mobi"          (Author - Title)
//   "The Star by Arthur C. Clarke.txt"                  (Title by Author)
//   "Martin, George RR - Ice and Fire 00 - The Hedge Knight.lit"
//                                                       (Last, First - Series NN - Title)
// A filename can't say which side is the author, so this returns EVERY plausible
// interpretation, most likely first; the caller keeps the first one whose author
// matches the OpenLibrary author catalogue. Network-free and deterministic.
public static class FilenameGuesser
{
    // "(mobi)" / "(epub)" / "(retail)" tags some sources append to the name —
    // possibly several stacked ("… (retail) (azw3)"), so stripped anywhere.
    private static readonly Regex FormatTag = new(
        @"\s*\((?:mobi|epub|pdf|azw3?|lit|rtf|txt|html?|fb2|docx|retail|v\d+(?:\.\d+)*)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A "[Series NN]" tag — at the start ("[Three Investigators 13] - The
    // Secret …") or as its own " - "-separated segment in the middle.
    private static readonly Regex LeadingSeriesTag = new(
        @"^\s*\[(?<s>.+?)\s+(?<p>\d{1,3})\]\s*-?\s*", RegexOptions.Compiled);
    private static readonly Regex SeriesTagSegment = new(
        @"^\[(?<s>.+?)\s+(?<p>\d{1,3})\]$", RegexOptions.Compiled);

    // "(1954)" — a publication year, not part of the title or author.
    private static readonly Regex YearTag = new(@"\s*\(\d{4}\)", RegexOptions.Compiled);

    // "Seafort 05" / "Ice and Fire 00" / "Pegasus 1" — a series segment.
    private static readonly Regex SeriesWithPosition = new(
        @"^(?<s>.+?)\s+(?<p>\d{1,3}(?:\.\d{1,2})?)$", RegexOptions.Compiled);

    // "Martin, George RR" — inverted personal name. The part after the comma must
    // not be an article ("Barbarian, The" is an inverted TITLE, not a name).
    private static readonly Regex InvertedName = new(
        @"^(?<last>[^,]{2,40}),\s*(?<first>[^,]{1,40})$", RegexOptions.Compiled);
    private static readonly HashSet<string> Articles = new(StringComparer.OrdinalIgnoreCase) { "the", "a", "an" };

    private static readonly Regex EtAl = new(@"[\s_,]+et\s+al\.?_?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MultiAuthorSeparator = new(
        @"\s*(?:;|&|\s+(?:and|AND|And|with)\s+)\s*", RegexOptions.Compiled);
    private static readonly Regex TitleByAuthor = new(
        @"^(?<t>.+)\s+(?:by|By|BY)\s+(?<a>[^-]+)$", RegexOptions.Compiled);

    // "Inferno (Troy Denning)" — the author tucked into trailing parentheses.
    private static readonly Regex TrailingParenAuthor = new(
        @"^(?<t>.+?)\s*\((?<a>[^()]{3,60})\)$", RegexOptions.Compiled);

    // Segments that are placeholder noise, not data — Calibre writes
    // "<something> - Unknown" when it has no title. Never a title, and
    // CRUCIALLY never an author: the OL catalogue contains literal "Unknown"/
    // "Anonymous" author records, so an unfiltered placeholder would validate.
    private static readonly HashSet<string> PlaceholderSegments =
        new(StringComparer.OrdinalIgnoreCase) { "Unknown", "Anonymous", "Anon", "Various", "Untitled" };

    public static IReadOnlyList<FilenameGuess> Interpret(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path)?.Trim() ?? "";
        if (name.Length < 3) return Array.Empty<FilenameGuess>();

        name = FormatTag.Replace(name, "").TrimEnd('_', ' ', '-');

        string? tagSeries = null, tagPos = null;
        var tag = LeadingSeriesTag.Match(name);
        if (tag.Success)
        {
            tagSeries = tag.Groups["s"].Value.Trim();
            tagPos = NormalisePosition(tag.Groups["p"].Value);
            name = name[tag.Length..];
        }

        var parts = Regex.Split(name, @"\s+-\s+").Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

        // Placeholder segments ("<X> - Unknown") carry no information — drop
        // them so the remaining segments are interpreted on their own merits
        // ("fiction by A. Author - Unknown" → the by-split sees a single part).
        parts.RemoveAll(p => PlaceholderSegments.Contains(p));
        if (parts.Count == 0) return Array.Empty<FilenameGuess>();

        // A "[Series NN]" segment anywhere claims the series and drops out; a
        // remaining segment that just repeats the series name is redundant too
        // ("The Three Investigators - [Three Investigators 34] - <Title> - <Author>").
        foreach (var part in parts.ToList())
        {
            var ts = SeriesTagSegment.Match(part);
            if (!ts.Success) continue;
            tagSeries = ts.Groups["s"].Value.Trim();
            tagPos = NormalisePosition(ts.Groups["p"].Value);
            parts.Remove(part);
            parts.RemoveAll(p =>
                string.Equals(p, tagSeries, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p, $"The {tagSeries}", StringComparison.OrdinalIgnoreCase));
            break;
        }

        var guesses = new List<FilenameGuess>();

        void Add(string? author, string? title, string? series = null, string? pos = null)
        {
            author = CleanAuthorCandidate(author);
            title = CleanTitleCandidate(title);
            if (author is null && title is null) return;
            var g = new FilenameGuess(author, title, series ?? tagSeries, pos ?? tagPos);
            if (!guesses.Contains(g)) guesses.Add(g);
        }

        // "Inferno (Troy Denning)" — an author tucked into trailing parentheses
        // on the last segment is the highest-confidence signal in the name, so
        // it goes first. The stripped segment doubles as the title.
        var paren = TrailingParenAuthor.Match(parts[^1]);
        if (paren.Success)
        {
            Add(paren.Groups["a"].Value, paren.Groups["t"].Value);
            parts[^1] = paren.Groups["t"].Value.Trim();
        }

        if (parts.Count == 1)
        {
            // "The Star by Arthur C. Clarke" — the rightmost " by " splits it.
            var m = TitleByAuthor.Match(parts[0]);
            if (m.Success) Add(m.Groups["a"].Value, m.Groups["t"].Value);

            // "Almuric Robert E. Howard" — title and author smashed together
            // with no separator at all. Probe trailing word groups as the
            // author (longest plausible name first so "Robert E. Howard" beats
            // "E. Howard"), then leading groups ("Nancy Kress Oaths"). Garbage
            // splits die at the caller's catalogue check.
            var words = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var n in new[] { 3, 2, 4 })
                if (words.Length >= n + 1)
                    Add(string.Join(' ', words[^n..]), string.Join(' ', words[..^n]));
            foreach (var n in new[] { 2, 3 })
                if (words.Length >= n + 1)
                    Add(string.Join(' ', words[..n]), string.Join(' ', words[n..]));

            // "Charles L. Harness-Lethary Fair" — author and title joined by a
            // hyphen WITHOUT spaces. Probe each such hyphen as the split point,
            // both orientations.
            var seen = 0;
            for (var i = 1; i < parts[0].Length - 1 && seen < 3; i++)
            {
                if (parts[0][i] != '-' || parts[0][i - 1] == ' ' || parts[0][i + 1] == ' ') continue;
                seen++;
                var left = parts[0][..i].Trim();
                var right = parts[0][(i + 1)..].Trim();
                if (left.Length < 3 || right.Length < 3) continue;
                Add(left, right);
                Add(right, left);
            }
        }
        else if (parts.Count == 2)
        {
            var (p, q) = (parts[0], parts[1]);
            // "Last, First - Title" is unambiguous — the comma marks the author.
            if (TryInvertName(p, out var inverted))
                Add(inverted, q);
            // Dominant download-name pattern: "Title - Author"…
            Add(q, p);
            // …but "Author - Title" exists too; the catalogue check disambiguates.
            Add(p, q);
            // "Pegasus 1 - Get Off The Unicorn" — series-positioned, author unknown.
            var sm = SeriesWithPosition.Match(p);
            if (sm.Success)
                Add(null, q, sm.Groups["s"].Value.Trim(), NormalisePosition(sm.Groups["p"].Value));
        }
        else if (parts.Count >= 3)
        {
            var first = parts[0];
            var last = parts[^1];

            // "03 - Fall of Kings - David; Stella Gemmell" — position, title, author.
            if (parts.Count == 3 && Regex.IsMatch(first, @"^\d{1,3}$"))
                Add(last, parts[1], null, NormalisePosition(first));

            // A middle segment naming the series ("Seafort 05").
            string? series = null, pos = null;
            foreach (var mid in parts.Skip(1).Take(parts.Count - 2))
            {
                var sm = SeriesWithPosition.Match(mid);
                if (sm.Success) { series = sm.Groups["s"].Value.Trim(); pos = NormalisePosition(sm.Groups["p"].Value); break; }
            }

            if (TryInvertName(first, out var inverted))
                Add(inverted, last, series, pos);
            Add(first, last, series, pos);
            Add(last, first, series, pos);
        }

        return guesses;
    }

    // "Martin, George RR" → "George RR Martin"; refuses inverted TITLES
    // ("Barbarian, The") and over-long segments that are clearly not names.
    private static bool TryInvertName(string segment, out string inverted)
    {
        inverted = "";
        var m = InvertedName.Match(segment.Trim());
        if (!m.Success) return false;
        var last = m.Groups["last"].Value.Trim();
        var first = m.Groups["first"].Value.Trim();
        if (Articles.Contains(first)) return false;
        if (last.Split(' ').Length > 3 || first.Split(' ').Length > 3) return false;
        inverted = $"{first} {last}";
        return true;
    }

    private static string? CleanAuthorCandidate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = YearTag.Replace(raw, " ").Replace('_', ' ');
        s = EtAl.Replace(s, "");
        // Multi-author credit → the first author (only one can be assigned). A
        // bare first name ("David; Stella Gemmell") borrows the last co-author's
        // surname — couples sharing a byline list the surname once, at the end.
        var authors = MultiAuthorSeparator.Split(s).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        if (authors.Count == 0) return null;
        s = authors[0];
        if (authors.Count > 1 && !s.Contains(' '))
        {
            var coWords = authors[^1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (coWords.Length >= 2) s = $"{s} {coWords[^1]}";
            // A lone word left over from splitting is a title fragment, not a
            // mononym — "Blood and Cupcakes" must not yield author "Blood".
            else return null;
        }
        s = Regex.Replace(s, @"\s+", " ").Trim().Trim(',', '.', '-').Trim();
        if (s.Length is < 3 or > 60) return null;
        if (PlaceholderSegments.Contains(s)) return null;
        if (!s.Any(char.IsLetter) || s.Any(char.IsDigit)) return null;
        var low = s.ToLowerInvariant();
        if (low.Contains("http") || low.Contains("www.") || low.Contains(".com")) return null;
        if (s.Split(' ').Length > 5) return null;
        return s;
    }

    private static string? CleanTitleCandidate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // "_" is how a sanitiser wrote ":" ("Honest Man_ A BWWM Romance");
        // a lone "_" elsewhere was some other illegal character — a space will do.
        var s = YearTag.Replace(raw, " ").Replace("_ ", ": ").Replace('_', ' ');
        s = Regex.Replace(s, @"\s+", " ").Trim();
        // "Barbarian, The" → "The Barbarian".
        var inv = Regex.Match(s, @"^(?<rest>.+),\s*(?<art>The|A|An)$", RegexOptions.IgnoreCase);
        if (inv.Success) s = $"{inv.Groups["art"].Value} {inv.Groups["rest"].Value}".Trim();
        var low = s.ToLowerInvariant();
        if (low.Contains("http") || low.Contains("www.")) return null;
        return s.Length is < 2 or > 200 ? null : s;
    }

    private static string NormalisePosition(string p)
        => int.TryParse(p, out var n) ? n.ToString() : p;
}

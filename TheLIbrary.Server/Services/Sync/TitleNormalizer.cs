using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TheLibrary.Server.Services.Sync;

public static class TitleNormalizer
{
    // Calibre appends "(123)" — strip the trailing id in parens.
    private static readonly Regex TrailingIdParens = new(@"\s*\(\d+\)\s*$", RegexOptions.Compiled);
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

    public static string NormalizeAuthor(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var s = StripDiacritics(input).ToLowerInvariant();
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
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}

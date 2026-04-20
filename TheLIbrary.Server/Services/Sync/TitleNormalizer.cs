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

    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var s = TrailingIdParens.Replace(input, "").Trim();
        s = StripDiacritics(s).ToLowerInvariant();
        foreach (var art in LeadingArticles)
            if (s.StartsWith(art)) { s = s[art.Length..]; break; }
        s = NonAlnum.Replace(s, " ").Trim();
        return Regex.Replace(s, @"\s+", " ");
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

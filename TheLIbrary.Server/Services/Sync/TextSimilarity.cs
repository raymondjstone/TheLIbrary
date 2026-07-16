using System.Text.RegularExpressions;

namespace TheLibrary.Server.Services.Sync;

// Word-set similarity for comparing extracted book text ACROSS FORMATS of what
// might be the same title — an EPUB vs. a PDF of the same book will have
// near-identical front/back-matter word content even though whitespace,
// line-wrapping and exact character positioning differ between extractors.
// Deliberately NOT FuzzyScore.JaroWinkler (that's edit-distance over SHORT
// strings — author/book names): this is Jaccard similarity over normalized
// word SETS, comparing which words appear rather than their order or exact
// position, so it's robust to reflow/OCR noise between formats.
public static class TextSimilarity
{
    private static readonly Regex WordRx = new(@"\p{L}+", RegexOptions.Compiled);

    // Percentage (0-100) of word overlap between two texts. Returns null —
    // "can't verify", NOT a similarity score of any kind — when either text
    // has fewer than minWords distinct words, e.g. because extraction failed
    // or the format yielded nothing usable. Callers must treat null the same
    // as "not matched": there's no evidence either way, so don't act on it.
    public static double? PercentSimilar(string? a, string? b, int minWords = 20)
    {
        var wordsA = Tokenize(a);
        var wordsB = Tokenize(b);
        if (wordsA.Count < minWords || wordsB.Count < minWords) return null;

        var intersection = wordsA.Intersect(wordsB).Count();
        var union = wordsA.Count + wordsB.Count - intersection;
        return union == 0 ? 100 : (double)intersection / union * 100;
    }

    private static HashSet<string> Tokenize(string? text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return set;
        foreach (Match m in WordRx.Matches(text))
            if (m.Value.Length >= 3) set.Add(m.Value.ToLowerInvariant()); // drop 1-2 letter noise (a, an, of, ...)
        return set;
    }
}

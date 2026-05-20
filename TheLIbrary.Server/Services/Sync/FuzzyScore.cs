namespace TheLibrary.Server.Services.Sync;

// Cheap-and-cheerful fuzzy similarity between two strings, returning a score
// in [0, 1]. Uses Jaro-Winkler: 1.0 means the strings are identical after
// normalisation; 0.85+ is "almost certainly the same"; below 0.7 is noise.
//
// Used when an exact normalised-title lookup misses but we still want to
// surface the closest candidates so the user can confirm or reject the match.
public static class FuzzyScore
{
    public static double JaroWinkler(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;
        if (string.Equals(a, b, StringComparison.Ordinal)) return 1.0;

        var jaro = Jaro(a!, b!);
        if (jaro < 0.7) return jaro;

        // Winkler boost: up to 4 leading matching characters lift the score
        // toward 1.0, since shared prefixes are a strong sameness signal.
        var prefix = 0;
        var max = Math.Min(4, Math.Min(a!.Length, b!.Length));
        for (var i = 0; i < max; i++)
        {
            if (a[i] != b[i]) break;
            prefix++;
        }
        return jaro + prefix * 0.1 * (1 - jaro);
    }

    private static double Jaro(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1.0;
        var matchDistance = Math.Max(a.Length, b.Length) / 2 - 1;
        if (matchDistance < 0) matchDistance = 0;

        var aMatches = new bool[a.Length];
        var bMatches = new bool[b.Length];
        var matches = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var start = Math.Max(0, i - matchDistance);
            var end = Math.Min(i + matchDistance + 1, b.Length);
            for (var j = start; j < end; j++)
            {
                if (bMatches[j] || a[i] != b[j]) continue;
                aMatches[i] = true;
                bMatches[j] = true;
                matches++;
                break;
            }
        }
        if (matches == 0) return 0.0;

        var transpositions = 0;
        var k = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (!aMatches[i]) continue;
            while (!bMatches[k]) k++;
            if (a[i] != b[k]) transpositions++;
            k++;
        }
        transpositions /= 2;

        var m = (double)matches;
        return (m / a.Length + m / b.Length + (m - transpositions) / m) / 3.0;
    }
}

using System.ComponentModel.DataAnnotations;

namespace TheLibrary.Server.Data.Models;

// One cached OpenLibrary resolution per ISBN (search.json?isbn=). Keyed by the
// normalized ISBN itself so EVERY file carrying that code shares one lookup —
// resolve once, and every other file with the same ISBN reuses it with no further
// OpenLibrary call. ResolvedAt is stamped on every attempt, including a miss (work
// fields left null), so a no-result ISBN isn't retried forever.
public class IsbnResolution
{
    // Normalized ISBN (digits + trailing X, from IsbnKey) — the primary key.
    [Key]
    [MaxLength(20)]
    public string Isbn { get; set; } = "";

    public DateTime ResolvedAt { get; set; }

    [MaxLength(50)] public string? WorkKey { get; set; }
    [MaxLength(500)] public string? Title { get; set; }
    [MaxLength(500)] public string? AuthorName { get; set; }
    [MaxLength(50)] public string? AuthorKey { get; set; }
    public int? FirstPublishYear { get; set; }
    public int? CoverId { get; set; }

    // Reduce a raw/hyphenated ISBN to its bare digits (plus a trailing X) so
    // "0-671-31993-0" and "0671319930" share one cache row. Returns null when the
    // result isn't a VALID ISBN — right length AND correct check digit. The check
    // digit matters: front-matter extraction often scrapes a 10/13-digit number that
    // isn't an ISBN at all (an LCCN, an ASIN, a catalogue or copyright-page number),
    // and those would otherwise burn an OpenLibrary call and store a meaningless miss.
    public static string? IsbnKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var clean = new string(raw.Where(c => char.IsDigit(c) || c is 'X' or 'x').ToArray()).ToUpperInvariant();
        return clean.Length switch
        {
            10 when IsValidIsbn10(clean) => clean,
            13 when IsValidIsbn13(clean) => clean,
            _ => null,
        };
    }

    private static bool IsValidIsbn10(string s)
    {
        var sum = 0;
        for (var i = 0; i < 10; i++)
        {
            int v;
            if (s[i] == 'X') { if (i != 9) return false; v = 10; }
            else if (char.IsDigit(s[i])) v = s[i] - '0';
            else return false;
            sum += (10 - i) * v;
        }
        return sum % 11 == 0;
    }

    private static bool IsValidIsbn13(string s)
    {
        var sum = 0;
        for (var i = 0; i < 13; i++)
        {
            if (!char.IsDigit(s[i])) return false;
            sum += (s[i] - '0') * (i % 2 == 0 ? 1 : 3);
        }
        return sum % 10 == 0;
    }
}

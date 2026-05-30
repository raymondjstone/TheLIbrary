using System.Globalization;

namespace TheLibrary.Server.Services.Sync;

// Best-effort "is this title in English?" guesser. Built to be conservative:
// it is far better to leave a title Unknown than to wrongly flag an English
// book as foreign (which would suppress it from the normal views). It uses no
// external models — three cheap, explainable stages:
//
//   1. Non-Latin script  → NonEnglish with high confidence (Greek, Cyrillic,
//      Hebrew, Arabic, CJK, Hangul, Thai, Vietnamese-extended, …). English
//      titles never contain these letters.
//   2. Function-word vote → for Latin-script titles, count well-known
//      non-English function words ("le/les/der/das/del/della/het/och" …) and
//      English function words ("the/and/of/with" …). The presence of English
//      function words is treated as decisive for English, because nearly every
//      multi-word English title has one.
//   3. Diacritic letters  → letters English never uses natively (ß ø å ñ ł …),
//      or a cluster of accented vowels, push an otherwise-ambiguous title to
//      NonEnglish.
//
// Single short titles with no signal (proper nouns like "Sapiens", "Persepolis")
// deliberately stay Unknown — there is no reliable way to classify them and a
// wrong guess is worse than none.
public static class TitleLanguageGuesser
{
    public enum Guess
    {
        // Looks like English, or has a clear English function word.
        English,
        // Non-Latin script, foreign function words, or strong foreign letters.
        NonEnglish,
        // Not enough signal to decide — caller should leave the book alone.
        Unknown
    }

    // Convenience wrapper used by the scan: only a confident NonEnglish counts.
    public static bool IsLikelyNonEnglish(string? title) => Classify(title) == Guess.NonEnglish;

    public static Guess Classify(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return Guess.Unknown;

        // --- Stage 1: any non-Latin letter is decisive -----------------------
        // 0x0370 is the start of the Greek block; everything from there up
        // (Cyrillic, Hebrew, Arabic, Indic, CJK, kana, Hangul, …) is a script
        // English does not use. Latin Extended Additional (Vietnamese, 0x1E00+)
        // is above this too, which is what we want — it is Latin letters but
        // unmistakably not English.
        foreach (var ch in title)
            if (char.IsLetter(ch) && ch >= 'Ͱ')
                return Guess.NonEnglish;

        // --- Stage 2: function-word vote -------------------------------------
        var tokens = Tokenize(title);
        if (tokens.Count == 0) return Guess.Unknown;

        var english = 0;
        var foreign = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in tokens)
        {
            if (EnglishStopwords.Contains(t)) english++;
            else if (ForeignStopwords.Contains(t)) foreign.Add(t);
        }

        // A single English function word is strong evidence of English and
        // wins outright — this is the guard that keeps real English titles from
        // ever being suppressed by the diacritic heuristics below.
        if (english >= 1) return Guess.English;

        // --- Stage 3: diacritic letters --------------------------------------
        var strongForeign = 0; // letters English never uses
        var softAccents = 0;   // accented vowels English occasionally borrows
        foreach (var ch in title)
        {
            var lower = char.ToLowerInvariant(ch);
            if (StrongForeignLetters.Contains(lower)) strongForeign++;
            else if (SoftAccentLetters.Contains(lower)) softAccents++;
        }

        // No English signal — decide from the foreign evidence we have.
        if (strongForeign >= 1) return Guess.NonEnglish;               // ß, ñ, ø, ł …
        if (foreign.Count >= 2) return Guess.NonEnglish;               // e.g. "el … los … del"
        if (foreign.Count >= 1 && softAccents >= 1) return Guess.NonEnglish; // "Les Misérables"
        if (softAccents >= 2) return Guess.NonEnglish;                 // multiple accents, no English

        return Guess.Unknown;
    }

    // Splits into lowercase letter-only tokens, preserving diacritics so the
    // accented stopwords (qué, für, são, …) still match.
    private static List<string> Tokenize(string title)
    {
        var tokens = new List<string>();
        var start = -1;
        for (var i = 0; i <= title.Length; i++)
        {
            var isLetter = i < title.Length && char.IsLetter(title[i]);
            if (isLetter && start < 0) start = i;
            else if (!isLetter && start >= 0)
            {
                tokens.Add(title[start..i].ToLowerInvariant());
                start = -1;
            }
        }
        return tokens;
    }

    // English function words. One hit decides the title is English. Content
    // words are deliberately excluded so foreign titles that happen to share a
    // loanword ("café", "saga") are not pulled in.
    private static readonly HashSet<string> EnglishStopwords = new(StringComparer.Ordinal)
    {
        "the", "and", "of", "to", "in", "is", "are", "was", "were", "with",
        "for", "on", "at", "by", "from", "as", "that", "this", "these", "those",
        "his", "her", "their", "our", "your", "my", "its", "it", "he", "she",
        "they", "we", "you", "but", "not", "all", "into", "over", "under",
        "after", "before", "between", "about", "against", "through", "what",
        "when", "where", "why", "how", "which", "who", "whom",
    };

    // Non-English function words with minimal overlap with real English words.
    // Collision-prone tokens (die, con, sin, den, over, door, war, was, as, men,
    // do, a, an, …) are intentionally left out so they can never false-flag an
    // English title.
    private static readonly HashSet<string> ForeignStopwords = new(StringComparer.Ordinal)
    {
        // French
        "le", "les", "un", "une", "des", "du", "de", "la", "et", "ne", "pas",
        "ce", "ces", "cette", "qui", "que", "quoi", "pour", "dans", "avec",
        "sur", "sous", "est", "sont", "être", "où", "très", "aussi", "leur",
        "leurs", "nous", "vous", "ils", "elles", "je", "tu", "mon", "ma", "mes",
        "ton", "tes", "ses", "plus", "moins", "jamais", "toujours", "déjà",
        "comme", "mais", "donc", "alors",
        // Spanish
        "el", "los", "las", "unos", "unas", "del", "para", "por", "como",
        "pero", "muy", "más", "también", "qué", "cómo", "dónde", "sí", "su",
        "sus", "este", "esta", "estos", "estas", "ese", "esa", "nada", "todo",
        "todos", "hacia", "desde", "entre", "según",
        // German
        "der", "das", "ein", "eine", "einen", "einem", "einer", "und", "nicht",
        "mit", "von", "zum", "zur", "auch", "aber", "oder", "durch", "über",
        "für", "sich", "wird", "sind", "im", "am", "dem", "des", "wie", "noch",
        "nur", "schon", "sehr", "mehr",
        // Italian
        "il", "lo", "gli", "uno", "della", "delle", "dei", "degli", "per",
        "anche", "perché", "che", "di", "non", "sono", "questo", "questa",
        "quello", "quella", "sulla", "nella", "dalla", "alla", "suo", "sua",
        // Portuguese
        "os", "um", "uma", "da", "dos", "das", "não", "são", "em", "seu",
        "isso", "esse", "essa", "muito", "pelo", "pela",
        // Dutch
        "het", "een", "van", "met", "voor", "niet", "ook", "maar", "deze",
        "zijn", "naar", "wordt", "werd",
        // Scandinavian
        "och", "att", "för", "är", "inte", "ett", "av", "på", "som", "han",
        "hon", "vara", "från", "eller", "även", "mycket",
    };

    // Letters that essentially never appear in English words — any one of them
    // (with no English function word present) marks the title non-English.
    private static readonly HashSet<char> StrongForeignLetters = new()
    {
        'ß', 'ø', 'å', 'æ', 'ñ', 'ð', 'þ', 'ł', 'đ', 'ş', 'ğ', 'ż', 'ź', 'ć',
        'š', 'ž', 'č', 'ř', 'ů', 'ő', 'ű', 'ą', 'ę', 'ı', 'ě', 'ń', 'ś', 'ý',
    };

    // Accented vowels English occasionally borrows (café, naïve, Pokémon). One
    // alone is not enough; two, or one plus a foreign function word, is.
    private static readonly HashSet<char> SoftAccentLetters = new()
    {
        'à', 'á', 'â', 'ä', 'ã', 'è', 'é', 'ê', 'ë', 'ì', 'í', 'î', 'ï',
        'ò', 'ó', 'ô', 'ö', 'õ', 'ù', 'ú', 'û', 'ü', 'ç',
    };
}

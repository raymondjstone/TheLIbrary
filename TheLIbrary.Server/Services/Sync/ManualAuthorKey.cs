namespace TheLibrary.Server.Services.Sync;

// Authors the user adds by hand — people OpenLibrary doesn't list yet (or that
// we just haven't matched) — get a synthetic author key in place of a real OL
// one. It mirrors the OL "OLnnnnnnnnA" shape but starts with "XX" so a manual
// author can be told apart at a glance, kept out of the OL refresh (its key
// would 404), and promoted in place once OL is found to list them.
public static class ManualAuthorKey
{
    public const string Prefix = "XX";

    // True for author keys minted by the manual "add author" flow. Distinguished
    // from a manual WORK key by the trailing "A" (works end "W").
    public static bool IsManual(string? authorKey) =>
        !string.IsNullOrEmpty(authorKey)
        && authorKey.StartsWith(Prefix, StringComparison.Ordinal)
        && authorKey.EndsWith("A", StringComparison.Ordinal);

    // A fresh candidate key: "XX" + 8 digits + "A" (e.g. XX04827193A).
    // Callers must check it against the Authors table and retry on collision.
    public static string NewCandidate() =>
        $"{Prefix}{Random.Shared.Next(0, 100_000_000):D8}A";
}

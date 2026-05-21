namespace TheLibrary.Server.Services.Sync;

// Books the user catalogues by hand — works OpenLibrary doesn't list yet —
// get a synthetic work key in place of a real OL one. It mirrors the OL
// "OLnnnnnnnnW" shape but starts with "XX" so a manual entry can be told
// apart at a glance and promoted in place once OL picks the title up.
public static class ManualWorkKey
{
    public const string Prefix = "XX";

    // True for keys minted by the manual "add book" flow.
    public static bool IsManual(string? workKey) =>
        !string.IsNullOrEmpty(workKey)
        && workKey.StartsWith(Prefix, StringComparison.Ordinal);

    // A fresh candidate key: "XX" + 8 digits + "W" (e.g. XX04827193W).
    // Callers must check it against the Books table and retry on collision.
    public static string NewCandidate() =>
        $"{Prefix}{Random.Shared.Next(0, 100_000_000):D8}W";
}

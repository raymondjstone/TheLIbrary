namespace TheLibrary.Server.Services.Incoming;

// Pure-function decisions about which __unknown folders can now be matched
// against tracked authors. Disk I/O is deferred to the caller so this module
// stays unit-testable.
public static class UnknownFolderRecovery
{
    public sealed record FolderDecision(
        string FolderName,
        AuthorIndexEntry? Match);

    public sealed record RecoveryPlan(
        IReadOnlyList<FolderDecision> Matched,
        IReadOnlyList<string> Unmatched);

    // Probes each folder name against the matcher; returns the matched-author
    // bucket (with the chosen index entry per folder) and the leftover unmatched
    // names. A folder counts as matched only when its name resolves through
    // AuthorMatcher.TryGet — the variant expansion already handled by the
    // matcher covers "Last, First", forename rotations, and any AlternateNames
    // configured for the entry.
    public static RecoveryPlan Plan(
        IEnumerable<string> unknownFolderNames,
        AuthorMatcher matcher)
    {
        var matched = new List<FolderDecision>();
        var unmatched = new List<string>();
        foreach (var folder in unknownFolderNames)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;

            var hit = matcher.TryGet(folder);
            if (hit is not null && hit.IsTracked)
                matched.Add(new FolderDecision(folder, hit));
            else
                unmatched.Add(folder);
        }
        return new RecoveryPlan(matched, unmatched);
    }
}

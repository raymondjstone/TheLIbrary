using System.ComponentModel.DataAnnotations;

namespace TheLibrary.Server.Data.Models;

// Records that a specific file was explicitly unlinked from a specific book (the
// Duplicates page's "Unlink" button — a false match undone). Every automated
// matching path — sync's classic title match, assign-authors, resolve-works,
// promote-manual-books, the LLM jobs, and the bulk/manual "apply" flows — must
// refuse to re-link this exact (FullPath, BookId) pair, so a bad match doesn't keep
// getting silently re-added. Keyed by path rather than LocalBookFile.Id so the block
// survives the file being unmatched and rescanned. Removing a row here (Settings →
// Blocked book links) is the only way to lift a block, e.g. if it turns out the
// match was actually correct.
public class BlockedBookLink
{
    public int Id { get; set; }

    [MaxLength(2048)]
    public string FullPath { get; set; } = "";

    public int BookId { get; set; }

    public DateTime BlockedAt { get; set; }
}

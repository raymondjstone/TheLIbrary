using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;

namespace TheLibrary.Server.Services.Sync;

// Every automated (and bulk/apply) path that links a LocalBookFile to a Book must
// consult this before setting BookId — a permanent "never re-link this exact file
// to this exact book" record created by the Duplicates page's "Unlink" action.
// Keyed by FullPath rather than LocalBookFile.Id so the block survives the file
// being unmatched and rescanned. Lifted only by removing the row (Settings →
// Blocked book links).
public static class LinkBlocklist
{
    // Same normalization SyncService uses for every other path comparison
    // (FormC + uppercase) — case-insensitive-but-case-preserving CIFS/NAS mounts
    // can hand back a differently-cased path for the same file between when it
    // was blocked and a later scan, and an ordinal comparison would silently miss.
    public static string Canon(string p) => p.Normalize(System.Text.NormalizationForm.FormC).ToUpperInvariant();

    public static async Task<bool> IsBlockedAsync(LibraryDbContext db, string? fullPath, int? bookId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || bookId is null) return false;
        var canon = Canon(fullPath);
        return await db.BlockedBookLinks.AsNoTracking()
            .AnyAsync(b => b.FullPath.ToUpper() == canon && b.BookId == bookId, ct);
    }

    // Bulk preload for hot loops (the classic sync's per-file reconciliation) that
    // can't afford one query per file. Keyed by the same canonical form IsBlockedAsync
    // compares against, so callers must canonicalize the path they look up with too.
    public static async Task<HashSet<(string Path, int BookId)>> LoadAllAsync(LibraryDbContext db, CancellationToken ct)
    {
        var rows = await db.BlockedBookLinks.AsNoTracking()
            .Select(b => new { b.FullPath, b.BookId })
            .ToListAsync(ct);
        return rows.Select(r => (Canon(r.FullPath), r.BookId)).ToHashSet();
    }

    // Records a new block. Idempotent — calling it twice for the same pair is a no-op.
    public static async Task BlockAsync(LibraryDbContext db, string fullPath, int bookId, CancellationToken ct)
    {
        var exists = await db.BlockedBookLinks.AnyAsync(b => b.FullPath == fullPath && b.BookId == bookId, ct);
        if (exists) return;
        db.BlockedBookLinks.Add(new BlockedBookLink { FullPath = fullPath, BookId = bookId, BlockedAt = DateTime.UtcNow });
    }
}

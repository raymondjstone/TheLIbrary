using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Services.Calibre;

// Indexes ebook files in the __unknown quarantine folder(s) into the
// UnknownFiles table. Run after the unknown folder is touched (incoming /
// reprocess-unknown / sync) so the missing-works match search can score these
// candidates straight from the DB, instead of recursively walking the
// filesystem on every request (which was far too slow).
//
// The quarantine can hold hundreds of thousands of files, so this does a full
// rebuild via SqlBulkCopy (like SyncService does for LocalBookFiles) rather than
// EF row-by-row inserts, which would never complete at that scale.
public static class UnknownFileIndexer
{
    // Diagnostic summary of a rescan — surfaced by the manual reindex endpoint
    // so it's obvious which roots were checked and how many files were seen.
    public sealed record RescanResult(
        IReadOnlyList<string> RootsChecked,
        IReadOnlyList<string> RootsMissing,
        int EbookFilesSeen,
        int Added,
        int Removed,
        int Total);

    // Recurses into subfolders but silently skips files/dirs we can't read,
    // instead of throwing on the first inaccessible directory (which would
    // abort the whole scan and leave the table empty). AttributesToSkip = 0 is
    // deliberate: the default skips Hidden|System, and on a NAS/Docker bind the
    // share or its subfolders can present as symlinks (ReparsePoint) — skipping
    // either would silently exclude the very files we're trying to index.
    private static readonly EnumerationOptions Recurse = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
    };

    public static async Task<RescanResult> RescanAsync(
        LibraryDbContext db,
        IReadOnlyList<string> libraryLocations,
        CancellationToken ct)
    {
        var roots = await UnknownFolderResolver.GetSourceRootsAsync(db, libraryLocations, ct);
        var missing = new List<string>();
        var now = DateTime.UtcNow;

        // Build the row set from disk, de-duped on FullPath (case-insensitive,
        // matching the unique index's collation) so bulk insert can't collide.
        // We don't stat each file (Length/LastWriteTime) — at ~half a million
        // files that's half a million extra network round-trips, and the match
        // search doesn't use those fields anyway.
        var byPath = new Dictionary<string, FileRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) { missing.Add(root); continue; }
            foreach (var file in Directory.EnumerateFiles(root, "*", Recurse))
            {
                ct.ThrowIfCancellationRequested();
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (!CalibreScanner.EbookExtensions.Contains(ext)) continue;
                var fi = new FileInfo(file);
                byPath[file] = new FileRow(
                    file,
                    Path.GetFileName(file),
                    TitleNormalizer.Normalize(Path.GetFileNameWithoutExtension(file)),
                    fi.Exists ? fi.Length : 0L,
                    fi.Exists ? fi.LastWriteTimeUtc : now);
            }
        }
        var rows = byPath.Values;
        var seen = rows.Count;

        var prior = await db.UnknownFiles.CountAsync(ct);

        // Full rebuild via bulk copy, in one transaction so readers never see a
        // half-built (or empty) index.
        var conn = (SqlConnection)db.Database.GetDbConnection();
        var wasOpen = conn.State == ConnectionState.Open;
        if (!wasOpen) await conn.OpenAsync(ct);
        try
        {
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

            await using (var del = new SqlCommand("DELETE FROM UnknownFiles", conn, tx) { CommandTimeout = 600 })
                await del.ExecuteNonQueryAsync(ct);

            if (seen > 0)
            {
                var dt = new DataTable();
                dt.Columns.Add("FullPath", typeof(string));
                dt.Columns.Add("FileName", typeof(string));
                dt.Columns.Add("NormalizedTitle", typeof(string));
                dt.Columns.Add("SizeBytes", typeof(long));
                dt.Columns.Add("ModifiedAt", typeof(DateTime));
                dt.Columns.Add("ScannedAt", typeof(DateTime));
                foreach (var r in rows)
                    dt.Rows.Add(r.FullPath, r.FileName,
                        (object?)r.NormalizedTitle ?? DBNull.Value, r.SizeBytes, r.ModifiedAt, now);

                using var bc = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
                {
                    DestinationTableName = "UnknownFiles",
                    BulkCopyTimeout = 600,
                    BatchSize = 10_000,
                };
                foreach (DataColumn c in dt.Columns) bc.ColumnMappings.Add(c.ColumnName, c.ColumnName);
                await bc.WriteToServerAsync(dt, ct);
            }

            await tx.CommitAsync(ct);
        }
        finally
        {
            if (!wasOpen) await conn.CloseAsync();
        }

        return new RescanResult(roots, missing, seen, seen, prior, seen);
    }

    private readonly record struct FileRow(string FullPath, string FileName, string? NormalizedTitle, long SizeBytes, DateTime ModifiedAt);
}

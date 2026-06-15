using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Controllers;

// One-click backup. Exports the curated, hard-to-regenerate data — config,
// the author watchlist, blacklist/ignore rules, series structure, manual books,
// and per-book user state (wanted/read/owned) — to a single downloadable ZIP of
// JSON files. Bulk catalogue data (OL works, the OL author dump, disk-scan rows)
// is intentionally excluded: it's rebuilt by a sync/seed. A guarded restore that
// re-applies this archive is a separate, explicitly-confirmed step (not here yet).
[ApiController]
[Route("api/[controller]")]
public class BackupController : ControllerBase
{
    private readonly LibraryDbContext _db;
    public BackupController(LibraryDbContext db) { _db = db; }

    public const int BackupVersion = 1;

    // GET /api/backup/export?manifest=true
    // `manifest` adds a (potentially large) list of every tracked local file
    // path — handy for verifying a restore, off by default to keep size down.
    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] bool manifest, CancellationToken ct)
    {
        var json = new JsonSerializerOptions { WriteIndented = true };

        var authors = await _db.Authors.AsNoTracking()
            .Select(a => new
            {
                a.Id, a.OpenLibraryKey, a.Name, a.CalibreFolderName, a.Status,
                a.ExclusionReason, a.WorkCount, a.Priority, a.LastSyncedAt, a.NextFetchAt,
                a.RefreshIntervalDays, a.CalibreScannedAt, a.Bio, a.Notes, a.NotifyOnNewBooks,
                a.LinkedToAuthorId, a.IsPenName,
            })
            .ToListAsync(ct);

        var locations = await _db.LibraryLocations.AsNoTracking().ToListAsync(ct);
        var settings = await _db.AppSettings.AsNoTracking().ToListAsync(ct);
        var blacklist = await _db.AuthorBlacklist.AsNoTracking().ToListAsync(ct);
        var ignored = await _db.IgnoredFolders.AsNoTracking().ToListAsync(ct);
        var nzb = await _db.NzbSites.AsNoTracking().ToListAsync(ct);
        var physical = await _db.PhysicalBookUnmatched.AsNoTracking().ToListAsync(ct);

        var series = await _db.Series.AsNoTracking()
            .Select(s => new { s.Id, s.Name, s.NormalizedName, s.PrimaryAuthorId, s.ParentSeriesId, s.PositionInParent })
            .ToListAsync(ct);
        var seriesAuthors = await _db.SeriesAuthors.AsNoTracking()
            .Select(sa => new { sa.SeriesId, sa.AuthorId })
            .ToListAsync(ct);

        // Books: only rows worth preserving — manual works (not on OL, can't be
        // re-fetched) and anything carrying user state (wanted / read / owned /
        // suppressed). Keyed by author OL key + work key so a restore can re-map.
        var allBookRows = await _db.Books.AsNoTracking()
            .Select(b => new
            {
                b.Id, b.AuthorId, AuthorOpenLibraryKey = b.Author.OpenLibraryKey,
                b.OpenLibraryWorkKey, b.Title, b.NormalizedTitle, b.FirstPublishYear,
                b.Subjects, b.SeriesId, b.SeriesPosition, b.Isbn, b.CoverId,
                b.Wanted, b.ReadStatus, b.ReadAt, b.ManuallyOwned, b.ManuallyOwnedAt, b.Suppressed,
            })
            .ToListAsync(ct);
        var bookRows = allBookRows
            .Where(b => ManualWorkKey.IsManual(b.OpenLibraryWorkKey)
                     || b.Wanted || b.ManuallyOwned || b.Suppressed
                     || b.ReadStatus != Data.Models.ReadStatus.Unread)
            .ToList();

        var meta = new
        {
            Version = BackupVersion,
            GeneratedAtUtc = DateTime.UtcNow,
            Counts = new
            {
                authors = authors.Count, books = bookRows.Count, series = series.Count,
                locations = locations.Count, settings = settings.Count,
                blacklist = blacklist.Count, ignored = ignored.Count, nzbSites = nzb.Count,
                physicalUnmatched = physical.Count,
            },
            IncludesManifest = manifest,
        };

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            async Task Write(string name, object data)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                await using var s = entry.Open();
                await JsonSerializer.SerializeAsync(s, data, data.GetType(), json, ct);
            }

            await Write("meta.json", meta);
            await Write("app_settings.json", settings);
            await Write("library_locations.json", locations);
            await Write("authors.json", authors);
            await Write("author_blacklist.json", blacklist);
            await Write("ignored_folders.json", ignored);
            await Write("nzb_sites.json", nzb);
            await Write("series.json", series);
            await Write("series_authors.json", seriesAuthors);
            await Write("books.json", bookRows);
            await Write("physical_unmatched.json", physical);

            if (manifest)
            {
                var files = await _db.LocalBookFiles.AsNoTracking()
                    .Select(f => new { f.FullPath, f.AuthorFolder, f.TitleFolder, f.BookId, f.AuthorId })
                    .ToListAsync(ct);
                await Write("file_manifest.json", files);
            }
        }

        ms.Position = 0;
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        return File(ms.ToArray(), "application/zip", $"thelibrary-backup-{stamp}.zip");
    }
}

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
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
                b.OwnedDifferentEdition, b.OwnedDifferentEditionAt,
            })
            .ToListAsync(ct);
        var bookRows = allBookRows
            .Where(b => ManualWorkKey.IsManual(b.OpenLibraryWorkKey)
                     || b.Wanted || b.ManuallyOwned || b.OwnedDifferentEdition || b.Suppressed
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

    // ---- Restore --------------------------------------------------------------

    // Parse shapes mirroring the export projections. Enums were serialized as
    // numbers, so Status / ReadStatus come back as ints.
    private sealed record SettingRow(string Key, string? Value);
    private sealed record LocationRow(string? Label, string Path, bool Enabled, bool IsPrimary);
    private sealed record NzbRow(string Name, string UrlTemplate, int Order, bool Active);
    private sealed record BlacklistRow(string Name, string NormalizedName, string? FolderName, string? Reason);
    private sealed record IgnoredRow(string Name);
    private sealed record AuthorRow(
        int Id, string? OpenLibraryKey, string Name, string? CalibreFolderName, int Status,
        string? ExclusionReason, int? WorkCount, int Priority, DateTime? LastSyncedAt, DateTime? NextFetchAt,
        int? RefreshIntervalDays, DateTime? CalibreScannedAt, string? Bio, string? Notes, bool NotifyOnNewBooks,
        int? LinkedToAuthorId, bool IsPenName);
    private sealed record SeriesRow(int Id, string Name, string NormalizedName, int? PrimaryAuthorId, int? ParentSeriesId, string? PositionInParent);
    private sealed record SeriesAuthorRow(int SeriesId, int AuthorId);
    private sealed record BookRow(
        int Id, int AuthorId, string? AuthorOpenLibraryKey, string? OpenLibraryWorkKey, string Title,
        string? NormalizedTitle, int? FirstPublishYear, string? Subjects, int? SeriesId, string? SeriesPosition,
        string? Isbn, int? CoverId, bool Wanted, int ReadStatus, DateTime? ReadAt, bool ManuallyOwned,
        DateTime? ManuallyOwnedAt, bool Suppressed,
        bool OwnedDifferentEdition = false, DateTime? OwnedDifferentEditionAt = null);
    private sealed record PhysicalRow(string Author, string Title, string SeriesPos, string? Isbn, DateTime AddedAt);

    public sealed record ImportSummary(
        int Settings, int Locations, int NzbSites, int Blacklist, int IgnoredFolders,
        int AuthorsCreated, int AuthorsUpdated, int Series, int SeriesAuthors,
        int BooksCreated, int BooksUpdated, int Physical, IReadOnlyList<string> Warnings);

    // POST /api/backup/import  (multipart: file = the backup .zip)
    // Merge-restore by natural keys (OL key / normalized name / work key) so it
    // works even after a full rebuild where the original IDs are gone. Existing
    // rows are updated in place; nothing is deleted. Atomic (execution-strategy
    // transaction) — either the whole archive applies or none of it does.
    [HttpPost("import")]
    [RequestSizeLimit(200_000_000)]
    public async Task<ActionResult<ImportSummary>> Import([FromForm] IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "No backup file uploaded." });

        var opt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        List<SettingRow> settings; List<LocationRow> locations; List<NzbRow> nzb;
        List<BlacklistRow> blacklist; List<IgnoredRow> ignored; List<AuthorRow> authors;
        List<SeriesRow> series; List<SeriesAuthorRow> seriesAuthors; List<BookRow> books; List<PhysicalRow> physical;
        try
        {
            using var zip = new ZipArchive(file.OpenReadStream(), ZipArchiveMode.Read);
            if (zip.GetEntry("meta.json") is null)
                return BadRequest(new { error = "This doesn't look like a TheLibrary backup (no meta.json)." });

            async Task<List<T>> Read<T>(string name)
            {
                var e = zip.GetEntry(name);
                if (e is null) return new();
                await using var s = e.Open();
                return await JsonSerializer.DeserializeAsync<List<T>>(s, opt, ct) ?? new();
            }

            settings = await Read<SettingRow>("app_settings.json");
            locations = await Read<LocationRow>("library_locations.json");
            nzb = await Read<NzbRow>("nzb_sites.json");
            blacklist = await Read<BlacklistRow>("author_blacklist.json");
            ignored = await Read<IgnoredRow>("ignored_folders.json");
            authors = await Read<AuthorRow>("authors.json");
            series = await Read<SeriesRow>("series.json");
            seriesAuthors = await Read<SeriesAuthorRow>("series_authors.json");
            books = await Read<BookRow>("books.json");
            physical = await Read<PhysicalRow>("physical_unmatched.json");
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = $"Could not read backup archive: {ex.Message}" });
        }

        var warnings = new List<string>();
        var counts = new int[12];

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // 1. Settings / locations / nzb / blacklist / ignored — simple upserts.
            var exSettings = await _db.AppSettings.ToDictionaryAsync(s => s.Key, s => s, StringComparer.OrdinalIgnoreCase, ct);
            foreach (var r in settings)
            {
                if (string.IsNullOrWhiteSpace(r.Key)) continue;
                if (exSettings.TryGetValue(r.Key, out var s)) s.Value = r.Value ?? "";
                else _db.AppSettings.Add(new AppSetting { Key = r.Key, Value = r.Value ?? "" });
                counts[0]++;
            }

            var exLoc = await _db.LibraryLocations.ToDictionaryAsync(l => l.Path, l => l, StringComparer.OrdinalIgnoreCase, ct);
            foreach (var r in locations)
            {
                if (string.IsNullOrWhiteSpace(r.Path)) continue;
                if (exLoc.TryGetValue(r.Path, out var l)) { l.Label = r.Label; l.Enabled = r.Enabled; l.IsPrimary = r.IsPrimary; }
                else _db.LibraryLocations.Add(new LibraryLocation { Path = r.Path, Label = r.Label, Enabled = r.Enabled, IsPrimary = r.IsPrimary, CreatedAt = DateTime.UtcNow });
                counts[1]++;
            }

            var exNzb = await _db.NzbSites.ToDictionaryAsync(n => n.Name, n => n, StringComparer.OrdinalIgnoreCase, ct);
            foreach (var r in nzb)
            {
                if (string.IsNullOrWhiteSpace(r.Name)) continue;
                if (exNzb.TryGetValue(r.Name, out var n)) { n.UrlTemplate = r.UrlTemplate; n.Order = r.Order; n.Active = r.Active; }
                else _db.NzbSites.Add(new NzbSite { Name = r.Name, UrlTemplate = r.UrlTemplate, Order = r.Order, Active = r.Active });
                counts[2]++;
            }

            var exBl = await _db.AuthorBlacklist.ToDictionaryAsync(b => b.NormalizedName, b => b, StringComparer.OrdinalIgnoreCase, ct);
            foreach (var r in blacklist)
            {
                var norm = string.IsNullOrWhiteSpace(r.NormalizedName) ? TitleNormalizer.NormalizeAuthor(r.Name) : r.NormalizedName;
                if (string.IsNullOrWhiteSpace(norm)) continue;
                if (exBl.TryGetValue(norm, out var b)) { b.FolderName = r.FolderName; b.Reason = r.Reason; }
                else { var nb = new AuthorBlacklist { Name = r.Name, NormalizedName = norm, FolderName = r.FolderName, Reason = r.Reason, AddedAt = DateTime.UtcNow }; _db.AuthorBlacklist.Add(nb); exBl[norm] = nb; }
                counts[3]++;
            }

            var exIg = await _db.IgnoredFolders.ToDictionaryAsync(i => i.Name, i => i, StringComparer.OrdinalIgnoreCase, ct);
            foreach (var r in ignored)
            {
                if (string.IsNullOrWhiteSpace(r.Name) || exIg.ContainsKey(r.Name)) continue;
                var ni = new IgnoredFolder { Name = r.Name, CreatedAt = DateTime.UtcNow };
                _db.IgnoredFolders.Add(ni); exIg[r.Name] = ni; counts[4]++;
            }
            await _db.SaveChangesAsync(ct);

            // 2. Authors — match by OL key, else by name. Remap exported Id → entity.
            var existingAuthors = await _db.Authors.ToListAsync(ct);
            var byKey = existingAuthors.Where(a => !string.IsNullOrWhiteSpace(a.OpenLibraryKey))
                .GroupBy(a => a.OpenLibraryKey!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var byName = existingAuthors.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var authorMap = new Dictionary<int, Author>();

            foreach (var r in authors)
            {
                Author? a = null;
                if (!string.IsNullOrWhiteSpace(r.OpenLibraryKey)) byKey.TryGetValue(r.OpenLibraryKey!, out a);
                if (a is null) byName.TryGetValue(r.Name, out a);
                if (a is null)
                {
                    a = new Author { Name = r.Name, OpenLibraryKey = r.OpenLibraryKey, CreationSource = "restore" };
                    _db.Authors.Add(a);
                    if (!string.IsNullOrWhiteSpace(r.OpenLibraryKey)) byKey[r.OpenLibraryKey!] = a;
                    byName[r.Name] = a;
                    counts[5]++;
                }
                else counts[6]++;

                a.CalibreFolderName = r.CalibreFolderName;
                a.Status = (AuthorStatus)r.Status;
                a.ExclusionReason = r.ExclusionReason;
                a.WorkCount = r.WorkCount;
                a.Priority = r.Priority;
                a.LastSyncedAt = r.LastSyncedAt;
                a.NextFetchAt = r.NextFetchAt;
                a.RefreshIntervalDays = r.RefreshIntervalDays;
                a.CalibreScannedAt = r.CalibreScannedAt;
                a.Bio = r.Bio;
                a.Notes = r.Notes;
                a.NotifyOnNewBooks = r.NotifyOnNewBooks;
                a.IsPenName = r.IsPenName;
                authorMap[r.Id] = a;
            }
            await _db.SaveChangesAsync(ct);

            // Second pass: re-link LinkedToAuthorId via the id map.
            foreach (var r in authors.Where(x => x.LinkedToAuthorId.HasValue))
                if (authorMap.TryGetValue(r.Id, out var a) && authorMap.TryGetValue(r.LinkedToAuthorId!.Value, out var target))
                    a.LinkedToAuthorId = target.Id;
            await _db.SaveChangesAsync(ct);

            // 3. Series — match by normalized name; remap primary author / parent.
            var existingSeries = await _db.Series.ToListAsync(ct);
            var seriesByNorm = existingSeries.GroupBy(s => s.NormalizedName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var seriesMap = new Dictionary<int, Series>();
            foreach (var r in series)
            {
                var norm = string.IsNullOrWhiteSpace(r.NormalizedName) ? TitleNormalizer.Normalize(r.Name) : r.NormalizedName;
                if (!seriesByNorm.TryGetValue(norm, out var s))
                {
                    s = new Series { Name = r.Name, NormalizedName = norm };
                    _db.Series.Add(s); seriesByNorm[norm] = s;
                }
                s.PositionInParent = r.PositionInParent;
                s.PrimaryAuthorId = r.PrimaryAuthorId.HasValue && authorMap.TryGetValue(r.PrimaryAuthorId.Value, out var pa) ? pa.Id : null;
                seriesMap[r.Id] = s;
                counts[7]++;
            }
            await _db.SaveChangesAsync(ct);
            foreach (var r in series.Where(x => x.ParentSeriesId.HasValue))
                if (seriesMap.TryGetValue(r.Id, out var s) && seriesMap.TryGetValue(r.ParentSeriesId!.Value, out var parent))
                    s.ParentSeriesId = parent.Id;
            await _db.SaveChangesAsync(ct);

            // 4. SeriesAuthors.
            foreach (var r in seriesAuthors)
            {
                if (!seriesMap.TryGetValue(r.SeriesId, out var s) || !authorMap.TryGetValue(r.AuthorId, out var a)) continue;
                if (!await _db.SeriesAuthors.AnyAsync(x => x.SeriesId == s.Id && x.AuthorId == a.Id, ct))
                { _db.SeriesAuthors.Add(new SeriesAuthor { SeriesId = s.Id, AuthorId = a.Id }); counts[8]++; }
            }
            await _db.SaveChangesAsync(ct);

            // 5. Books — re-apply user state; create manual / missing rows.
            var workKeys = books.Select(b => b.OpenLibraryWorkKey).Where(k => k != null).Distinct().ToList();
            var existingBooks = await _db.Books.Where(b => workKeys.Contains(b.OpenLibraryWorkKey)).ToListAsync(ct);
            var bookIndex = existingBooks
                .GroupBy(b => (b.AuthorId, b.OpenLibraryWorkKey))
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var r in books)
            {
                if (!authorMap.TryGetValue(r.AuthorId, out var a))
                {
                    if (!string.IsNullOrWhiteSpace(r.AuthorOpenLibraryKey)) byKey.TryGetValue(r.AuthorOpenLibraryKey!, out a);
                    if (a is null) { warnings.Add($"Skipped book \"{r.Title}\" — author not found."); continue; }
                }
                var seriesId = r.SeriesId.HasValue && seriesMap.TryGetValue(r.SeriesId.Value, out var sm) ? sm.Id : (int?)null;

                if (bookIndex.TryGetValue((a.Id, r.OpenLibraryWorkKey ?? ""), out var b))
                {
                    b.Wanted = r.Wanted; b.ReadStatus = (ReadStatus)r.ReadStatus; b.ReadAt = r.ReadAt;
                    b.ManuallyOwned = r.ManuallyOwned; b.ManuallyOwnedAt = r.ManuallyOwnedAt; b.Suppressed = r.Suppressed;
                    b.OwnedDifferentEdition = r.OwnedDifferentEdition; b.OwnedDifferentEditionAt = r.OwnedDifferentEditionAt;
                    if (seriesId.HasValue) { b.SeriesId = seriesId; b.SeriesPosition = r.SeriesPosition; }
                    counts[10]++;
                }
                else
                {
                    var nb = new Book
                    {
                        AuthorId = a.Id, OpenLibraryWorkKey = r.OpenLibraryWorkKey ?? "",
                        Title = r.Title, NormalizedTitle = r.NormalizedTitle ?? TitleNormalizer.Normalize(r.Title),
                        FirstPublishYear = r.FirstPublishYear, Subjects = r.Subjects, SeriesId = seriesId,
                        SeriesPosition = r.SeriesPosition, Isbn = r.Isbn, CoverId = r.CoverId,
                        Wanted = r.Wanted, ReadStatus = (ReadStatus)r.ReadStatus, ReadAt = r.ReadAt,
                        ManuallyOwned = r.ManuallyOwned, ManuallyOwnedAt = r.ManuallyOwnedAt, Suppressed = r.Suppressed,
                        OwnedDifferentEdition = r.OwnedDifferentEdition, OwnedDifferentEditionAt = r.OwnedDifferentEditionAt,
                        // Backups don't carry CreatedAt; date a restored book with a
                        // past publish year to 1 Jan of that year (not "now") so it
                        // doesn't resurface as a new release after a restore.
                        CreatedAt = Book.CreatedAtForPublishYear(r.FirstPublishYear),
                    };
                    _db.Books.Add(nb);
                    bookIndex[(a.Id, nb.OpenLibraryWorkKey)] = nb;
                    counts[9]++;
                }
            }
            await _db.SaveChangesAsync(ct);

            // 6. Physical-import rows — insert ones not already present.
            var exPhysical = (await _db.PhysicalBookUnmatched.AsNoTracking()
                .Select(p => new { p.Author, p.Title }).ToListAsync(ct))
                .Select(p => $"{p.Author}\0{p.Title}".ToLowerInvariant()).ToHashSet();
            foreach (var r in physical)
            {
                var key = $"{r.Author}\0{r.Title}".ToLowerInvariant();
                if (exPhysical.Contains(key)) continue;
                exPhysical.Add(key);
                _db.PhysicalBookUnmatched.Add(new PhysicalBookUnmatched { Author = r.Author, Title = r.Title, SeriesPos = r.SeriesPos ?? "", Isbn = r.Isbn, AddedAt = DateTime.UtcNow });
                counts[11]++;
            }
            await _db.SaveChangesAsync(ct);

            await tx.CommitAsync(ct);
        });

        return Ok(new ImportSummary(
            counts[0], counts[1], counts[2], counts[3], counts[4], counts[5], counts[6],
            counts[7], counts[8], counts[9], counts[10], counts[11], warnings));
    }
}

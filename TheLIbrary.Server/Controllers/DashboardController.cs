using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;

namespace TheLibrary.Server.Controllers;

// Count summary for the Home dashboard. The book counts scan a ~1.8M-row table,
// so the whole result is cached for a short TTL — repeated Home loads are then
// free, and the cards never need to be real-time.
[ApiController]
[Route("api/[controller]")]
public class DashboardController : ControllerBase
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private readonly LibraryDbContext _db;
    private readonly IMemoryCache _cache;
    public DashboardController(LibraryDbContext db, IMemoryCache cache) { _db = db; _cache = cache; }

    public sealed record DashboardSummary(
        int TotalAuthors,
        int ActiveAuthors,
        int AuthorsDueRefresh,
        int OwnedBooks,
        int MissingBooks,
        int WantedBooks,
        int DamagedFiles,
        int UnclaimedFolders,
        int UnknownFiles,
        int ReleasesThisYear,
        int AddedThisWeek);

    [HttpGet]
    public Task<DashboardSummary> Get(CancellationToken ct)
        => _cache.GetOrCreateAsync("dashboard:summary", e =>
        {
            e.AbsoluteExpirationRelativeToNow = CacheTtl;
            return ComputeAsync(ct);
        })!;

    private async Task<DashboardSummary> ComputeAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var year = now.Year;
        var weekAgo = now.AddDays(-7);

        var totalAuthors = await _db.Authors.CountAsync(ct);
        var activeAuthors = await _db.Authors.CountAsync(a => a.Status == AuthorStatus.Active, ct);
        // "Due for refresh" mirrors AuthorRefresher's selection: an active author
        // that has never been fetched (NextFetchAt == null) or whose next-fetch
        // time has passed.
        var authorsDueRefresh = await _db.Authors.CountAsync(
            a => a.Status == AuthorStatus.Active
              && (a.NextFetchAt == null || a.NextFetchAt <= now), ct);

        // All four book figures in a single table scan instead of four separate
        // COUNTs, each with its own owned/EXISTS semi-join over ~1.8M rows.
        var books = await _db.Books.GroupBy(_ => 1).Select(g => new
        {
            Owned = g.Count(b => b.ManuallyOwned || b.LocalFiles.Any()),
            Missing = g.Count(b => !b.ManuallyOwned && !b.LocalFiles.Any()),
            Wanted = g.Count(b => b.Wanted && !b.ManuallyOwned && !b.LocalFiles.Any()),
            ReleasesThisYear = g.Count(b => !b.Suppressed && b.FirstPublishYear == year),
        }).FirstOrDefaultAsync(ct);
        var ownedBooks = books?.Owned ?? 0;
        var missingBooks = books?.Missing ?? 0;
        var wantedBooks = books?.Wanted ?? 0;
        var releasesThisYear = books?.ReleasesThisYear ?? 0;

        // Damaged: same rule as the Damaged page — IntegrityOk == false, not under
        // the archive folder, real ebook files only. The IsEbook test isn't
        // SQL-translatable, so pull the (small) damaged path set and filter.
        var archiveLeaf = await GetArchiveLeafAsync(ct);
        var damagedPaths = await _db.LocalBookFiles.AsNoTracking()
            .Where(f => f.IntegrityOk == false)
            .Where(ArchivedFilesController.NotUnderArchive(archiveLeaf))
            .Select(f => f.FullPath)
            .ToListAsync(ct);
        var damagedFiles = damagedPaths.Count(BookIntegrityChecker.IsEbook);

        // Untracked backlog: distinct library folders not yet linked to an author,
        // plus the count of loose files indexed in the __unknown quarantine.
        var unclaimedFolders = await _db.LocalBookFiles.AsNoTracking()
            .Where(f => f.AuthorId == null)
            .Select(f => f.AuthorFolder)
            .Distinct()
            .CountAsync(ct);
        var unknownFiles = await _db.UnknownFiles.CountAsync(ct);

        // Genuinely weekly: local files acquired in the last 7 days.
        var addedThisWeek = await _db.LocalBookFiles.CountAsync(f => f.ModifiedAt >= weekAgo, ct);

        return new DashboardSummary(
            totalAuthors, activeAuthors, authorsDueRefresh,
            ownedBooks, missingBooks, wantedBooks,
            damagedFiles, unclaimedFolders, unknownFiles,
            releasesThisYear, addedThisWeek);
    }

    private async Task<string> GetArchiveLeafAsync(CancellationToken ct)
    {
        var stored = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.DedupeArchiveFolder)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(stored) ? "__archive" : stored.Trim();
    }
}

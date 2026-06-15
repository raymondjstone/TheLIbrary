using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;

namespace TheLibrary.Server.Controllers;

// Lightweight count-only summary for the Home dashboard. Every figure is a
// COUNT (or a small path-set filter) so the landing page stays fast even on a
// large library — no per-book scans, no disk I/O.
[ApiController]
[Route("api/[controller]")]
public class DashboardController : ControllerBase
{
    private readonly LibraryDbContext _db;
    public DashboardController(LibraryDbContext db) { _db = db; }

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
    public async Task<DashboardSummary> Get(CancellationToken ct)
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

        var ownedBooks = await _db.Books.CountAsync(b => b.ManuallyOwned || b.LocalFiles.Any(), ct);
        var missingBooks = await _db.Books.CountAsync(b => !b.ManuallyOwned && !b.LocalFiles.Any(), ct);
        var wantedBooks = await _db.Books.CountAsync(
            b => b.Wanted && !b.ManuallyOwned && !b.LocalFiles.Any(), ct);

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

        // New releases: works first published in the current calendar year for any
        // tracked author (year is the finest publish granularity OpenLibrary gives).
        var releasesThisYear = await _db.Books.CountAsync(
            b => !b.Suppressed && b.FirstPublishYear == year, ct);

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

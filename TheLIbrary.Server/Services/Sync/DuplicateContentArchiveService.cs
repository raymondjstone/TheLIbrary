using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.IO;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

public sealed record DuplicateContentArchiveSummary(
    int GroupsConsidered, int GroupsArchived, int GroupsSkippedMismatch, int FilesArchived, int Warnings);

// Scheduled job (OFF by default): for every book with more than one live copy,
// extracts and compares each copy's TEXT CONTENT (front+back matter, via
// BookTextReader — the same extractor full-text search uses) before archiving
// anything. Only when EVERY extra copy's content matches the keeper's within
// the configured percentage (TextSimilarity — word-set overlap, robust to
// format-specific reflow/OCR noise) does it perform the equivalent of the
// Duplicates page's "Archive extras". If ANY copy's content can't be confirmed
// similar enough — including when extraction fails or yields too little text to
// judge — the ENTIRE group is left completely untouched for manual review; a
// close call never gets resolved by guessing.
//
// This is deliberately more cautious than, and independent of,
// DuplicateAutoArchiveService (which archives on format/integrity ALONE, no
// content check). Whichever of the two runs first and collapses a group simply
// leaves the other with nothing left to do on it — they don't need to
// coordinate beyond the usual single-worker scheduler exclusion.
public sealed class DuplicateContentArchiveService
{
    public const int MaxPerRun = 100; // groups/run — text extraction is comparatively expensive
    public const double DefaultMatchThresholdPercent = 90;
    private const int MaxCharsPerFile = 50_000; // head+tail budget per file, per BookTextReader call
    private const int MinWordsToCompare = 20;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly BookTextReader _textReader;
    private readonly IFileSystem _fs;
    private readonly ILogger<DuplicateContentArchiveService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;
    private DuplicateContentArchiveSummary? _lastResult;

    public DuplicateContentArchiveService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        BookTextReader textReader,
        IFileSystem fs,
        ILogger<DuplicateContentArchiveService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _textReader = textReader;
        _fs = fs;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;
    public DuplicateContentArchiveSummary? LastResult => _lastResult;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("verify-archive-duplicates", out var holder))
        {
            error = $"Another task is already running ({holder})";
            return false;
        }
        error = null;
        _isRunning = true;
        _ = Task.Run(async () =>
        {
            try { _lastResult = await RunAsync(hostCt); }
            catch (OperationCanceledException) when (hostCt.IsCancellationRequested) { }
            catch (Exception ex) { _log.LogError(ex, "Content-verified duplicate archive failed"); }
            finally { _isRunning = false; _currentMessage = null; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    internal Task<DuplicateContentArchiveSummary> RunForTestsAsync(CancellationToken ct) => RunAsync(ct);

    private async Task<DuplicateContentArchiveSummary> RunAsync(CancellationToken ct)
    {
        _currentMessage = "Loading duplicate groups";
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

        var archiveLeaf = await ArchivePolicy.LoadLeafAsync(db, ct);
        var preference = await FormatPreference.LoadAsync(db, ct);
        var threshold = await LoadThresholdAsync(db, ct);
        var maxPerRun = await JobRunLimits.GetAsync(db, AppSettingKeys.VerifyArchiveDuplicatesMaxPerRun, MaxPerRun, ct);
        var locations = await db.LibraryLocations.AsNoTracking()
            .Where(l => l.Enabled).Select(l => l.Path).ToListAsync(ct);

        // Books with more than one LIVE (non-archived) file — same candidate
        // selection as DuplicateAutoArchiveService and the Duplicates page.
        var bookIds = await db.LocalBookFiles.AsNoTracking()
            .Where(f => f.BookId != null)
            .Where(ArchivePolicy.NotUnder(archiveLeaf))
            .GroupBy(f => f.BookId!.Value)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .Take(maxPerRun)
            .ToListAsync(ct);

        int considered = 0, groupsArchived = 0, groupsSkipped = 0, filesArchived = 0, warnings = 0;
        foreach (var bookId in bookIds)
        {
            ct.ThrowIfCancellationRequested();
            considered++;
            _currentMessage = $"Checking content {considered}/{bookIds.Count}";

            var files = await db.LocalBookFiles
                .Include(f => f.Book)
                .Where(f => f.BookId == bookId)
                .Where(ArchivePolicy.NotUnder(archiveLeaf))
                .ToListAsync(ct);

            // Resolve each row to a real, readable ebook copy; drop phantom/folder
            // rows so they never count as a copy — same rule as the Duplicates page.
            var copies = files
                .Select(f => new { File = f, Resolved = ResolveCopy(f.FullPath) })
                .Where(x => x.Resolved.IsReal)
                .ToList();
            if (copies.Count < 2) continue;

            var keeper = copies
                .OrderBy(c => c.File.IntegrityOk == false ? 1 : 0)
                .ThenBy(c => FormatPreference.Rank(c.Resolved.Format, preference))
                .ThenBy(c => c.File.Id)
                .First();
            var extras = copies.Where(c => c.File.Id != keeper.File.Id).ToList();

            var keeperText = await _textReader.ReadHeadAndTailAsync(keeper.File.FullPath, MaxCharsPerFile, ct);

            // Every extra must independently match the keeper — one mismatched (or
            // unverifiable) copy stops the WHOLE group, nothing gets archived. No
            // partial archiving: the point is "the whole set is confidently the same
            // book," not "some of them are." Every extra is still scored (not just
            // until the first miss) so the per-title activity line below shows the
            // real percentage for each copy, not just whichever one happened first —
            // that's what makes the log useful for judging the threshold.
            var allMatch = true;
            var scored = new List<(string FileName, double? Percent)>();
            foreach (var extra in extras)
            {
                ct.ThrowIfCancellationRequested();
                var extraText = await _textReader.ReadHeadAndTailAsync(extra.File.FullPath, MaxCharsPerFile, ct);
                var similarity = TextSimilarity.PercentSimilar(keeperText, extraText, MinWordsToCompare);
                scored.Add((Path.GetFileName(extra.File.FullPath), similarity));
                if (similarity is null || similarity < threshold) allMatch = false;
            }

            var title = keeper.File.Book?.Title ?? $"Book #{bookId}";
            var scoreText = string.Join(", ", scored.Select(s => $"{s.FileName}: {(s.Percent is null ? "n/a" : $"{s.Percent:0.#}%")}"));
            var verdict = allMatch ? "archived" : "left untouched — needs manual review";
            Services.ActivityLogger.Record(db, "verify-archive-duplicates",
                $"\"{title}\" — {scoreText} (need ≥{threshold:0.#}%) → {verdict}",
                source: "verify-archive-duplicates", bookId: bookId);
            await db.SaveChangesAsync(ct);

            if (!allMatch) { groupsSkipped++; continue; }

            groupsArchived++;
            foreach (var extra in extras)
            {
                if (await DuplicateFileArchiver.ArchiveAsync(extra.File, archiveLeaf, locations, _fs, _log, ct)) filesArchived++;
                else warnings++;
            }
            await db.SaveChangesAsync(ct);
        }

        if (filesArchived > 0)
        {
            Services.ActivityLogger.Record(db, "archive",
                $"Content-verified {filesArchived} duplicate extra(s) as matching (≥{threshold:0.#}% text overlap) and archived across {groupsArchived} book(s)",
                source: "verify-archive-duplicates");
            await db.SaveChangesAsync(ct);
        }

        var summary = new DuplicateContentArchiveSummary(considered, groupsArchived, groupsSkipped, filesArchived, warnings);
        _log.LogInformation(
            "verify-archive-duplicates done — considered {Considered}, archived {Archived} group(s) ({Files} file(s)), " +
            "skipped {Skipped} group(s) (content mismatch/unverifiable), {Warnings} warning(s)",
            considered, groupsArchived, groupsSkipped, filesArchived, warnings);
        _currentMessage = $"Done — archived {groupsArchived} of {considered} group(s), {groupsSkipped} skipped (content differs)";
        return summary;
    }

    private static async Task<double> LoadThresholdAsync(LibraryDbContext db, CancellationToken ct)
    {
        var raw = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.DuplicateContentMatchThreshold)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);
        return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pct)
            && pct is > 0 and <= 100 ? pct : DefaultMatchThresholdPercent;
    }

    // Mirrors DuplicateAutoArchiveService.ResolveCopy: a path is a real copy if
    // it's an existing ebook file (by extension), or a directory holding one. A
    // folder-shaped row's FullPath isn't a readable FILE, so BookTextReader
    // naturally returns "" for it — TextSimilarity then reports "can't verify"
    // and the group is skipped, the same safe fallback as an extraction failure.
    private (string? Format, bool IsReal) ResolveCopy(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return (null, false);
        if (_fs.FileExists(fullPath))
        {
            var e = Path.GetExtension(fullPath).TrimStart('.').ToLowerInvariant();
            return CalibreScanner.EbookExtensions.Contains("." + e) ? (e, true) : (null, false);
        }
        if (_fs.DirectoryExists(fullPath))
        {
            try
            {
                var fmt = _fs.EnumerateFiles(fullPath)
                    .Where(f => CalibreScanner.EbookExtensions.Contains(Path.GetExtension(f)))
                    .Select(f => Path.GetExtension(f).TrimStart('.').ToLowerInvariant())
                    .OrderBy(x => FormatPreference.Rank(x, FormatPreference.Default))
                    .FirstOrDefault();
                return (fmt, fmt is not null);
            }
            catch { return (null, false); }
        }
        return (null, false);
    }
}

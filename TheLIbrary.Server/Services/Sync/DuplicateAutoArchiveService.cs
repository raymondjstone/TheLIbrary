using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.IO;
using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Sync;

public sealed record DuplicateAutoArchiveSummary(int BooksProcessed, int FilesArchived, int Warnings);

// Scheduled job (OFF by default): for every book that has more than one live
// ebook copy, keep the single best copy and move the rest into the archive
// folder — the automated equivalent of clicking "Archive extras" on the
// Duplicates page for every book. "Best" matches the Duplicates page keeper rule:
// a healthy copy always beats a damaged one; among equals the preferred format
// wins (DuplicateFormatPreference), then the lowest row id. Archived rows are
// inert, so a previously-archived copy is never reconsidered.
//
// Every move goes through IFileSystem (SafeMove), which force-removes a lingering
// CIFS source so an archived copy can't be re-imported as a duplicate. The row is
// only repointed once the source is confirmed gone; otherwise it's left and warned.
public sealed class DuplicateAutoArchiveService
{
    private static readonly string[] DefaultFormatPreference =
        { "epub", "azw3", "azw", "mobi", "kfx", "fb2", "djvu", "pdf", "cbz", "cbr", "lit", "pdb", "rtf", "doc", "docx", "txt" };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly IFileSystem _fs;
    private readonly ILogger<DuplicateAutoArchiveService> _log;
    private volatile bool _isRunning;
    private volatile string? _currentMessage;
    private DuplicateAutoArchiveSummary? _lastResult;

    public DuplicateAutoArchiveService(
        IServiceScopeFactory scopeFactory,
        BackgroundTaskCoordinator coordinator,
        IFileSystem fs,
        ILogger<DuplicateAutoArchiveService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _fs = fs;
        _log = log;
    }

    public bool IsRunning => _isRunning;
    public string? CurrentMessage => _currentMessage;
    public DuplicateAutoArchiveSummary? LastResult => _lastResult;

    public bool TryStart(CancellationToken hostCt, out string? error)
    {
        if (!_coordinator.TryAcquire("dup-auto-archive", out var holder))
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
            catch (Exception ex) { _log.LogError(ex, "Duplicate auto-archive failed"); }
            finally { _isRunning = false; _currentMessage = null; _coordinator.Release(); }
        }, hostCt);
        return true;
    }

    internal Task<DuplicateAutoArchiveSummary> RunForTestsAsync(CancellationToken ct) => RunAsync(ct);

    private async Task<DuplicateAutoArchiveSummary> RunAsync(CancellationToken ct)
    {
        _currentMessage = "Loading duplicates";
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

        var archiveLeaf = await ArchivePolicy.LoadLeafAsync(db, ct);
        var preference = await LoadFormatPreferenceAsync(db, ct);
        var locations = await db.LibraryLocations.AsNoTracking()
            .Where(l => l.Enabled).Select(l => l.Path).ToListAsync(ct);

        // Books with more than one LIVE (non-archived) file. Archived rows are
        // excluded so we only ever collapse the live collection.
        var bookIds = await db.LocalBookFiles.AsNoTracking()
            .Where(f => f.BookId != null)
            .Where(ArchivePolicy.NotUnder(archiveLeaf))
            .GroupBy(f => f.BookId!.Value)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToListAsync(ct);

        int booksProcessed = 0, filesArchived = 0, warnings = 0;
        foreach (var bookId in bookIds)
        {
            ct.ThrowIfCancellationRequested();

            var files = await db.LocalBookFiles
                .Where(f => f.BookId == bookId)
                .Where(ArchivePolicy.NotUnder(archiveLeaf))
                .ToListAsync(ct);

            // Resolve each row to a real, readable ebook copy; drop phantom/folder
            // rows so they never count as a copy (or get archived).
            var copies = files
                .Select(f => new { File = f, Resolved = ResolveCopy(f.FullPath) })
                .Where(x => x.Resolved.IsReal)
                .ToList();
            if (copies.Count < 2) continue;

            var keeper = copies
                .OrderBy(c => c.File.IntegrityOk == false ? 1 : 0)
                .ThenBy(c => PreferenceRank(c.Resolved.Format, preference))
                .ThenBy(c => c.File.Id)
                .First();

            booksProcessed++;
            foreach (var extra in copies.Where(c => c.File.Id != keeper.File.Id))
            {
                _currentMessage = $"Archiving extra of book {bookId}";
                if (await DuplicateFileArchiver.ArchiveAsync(extra.File, archiveLeaf, locations, _fs, _log, ct)) filesArchived++;
                else warnings++;
            }
            await db.SaveChangesAsync(ct);
        }

        if (filesArchived > 0)
        {
            Services.ActivityLogger.Record(db, "auto-archive",
                $"Auto-archived {filesArchived} duplicate extra(s) across {booksProcessed} book(s)",
                source: "duplicate-auto-archive");
            await db.SaveChangesAsync(ct);
        }

        var summary = new DuplicateAutoArchiveSummary(booksProcessed, filesArchived, warnings);
        _log.LogInformation(
            "Duplicate auto-archive done. Books={Books} Archived={Archived} Warnings={Warnings}",
            booksProcessed, filesArchived, warnings);
        _currentMessage = $"Done — archived {filesArchived} extra(s) across {booksProcessed} book(s)";
        return summary;
    }

    // A path is a real copy if it's an existing ebook file (by extension), or a
    // directory holding a readable ebook. Returns the chosen format.
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
                    .OrderBy(x => PreferenceRank(x, DefaultFormatPreference))
                    .FirstOrDefault();
                return (fmt, fmt is not null);
            }
            catch { return (null, false); }
        }
        return (null, false);
    }

    private static int PreferenceRank(string? format, string[] preference)
    {
        if (string.IsNullOrEmpty(format)) return int.MaxValue;
        var idx = Array.IndexOf(preference, format.ToLowerInvariant());
        return idx < 0 ? int.MaxValue - 1 : idx;
    }

    private static async Task<string[]> LoadFormatPreferenceAsync(LibraryDbContext db, CancellationToken ct)
    {
        var raw = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.DuplicateFormatPreference)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(raw)) return DefaultFormatPreference;
        var parsed = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => f.TrimStart('.').ToLowerInvariant()).ToArray();
        return parsed.Length > 0 ? parsed : DefaultFormatPreference;
    }
}

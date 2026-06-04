using TheLibrary.Server.Services.IO;
using UglyToad.PdfPig;

namespace TheLibrary.Server.Services.Calibre;

public enum IntegrityStatus
{
    Ok = 0,
    Damaged = 1,
    // The file could not be checked this run for a reason that is not the
    // file's fault (e.g. Calibre is not configured so a non-native format
    // can't be converted). The caller should leave the record untouched and
    // retry on a later run rather than flagging the book as damaged.
    Skipped = 2,
}

public sealed record IntegrityResult(IntegrityStatus Status, int? Pages, string? Error)
{
    public static IntegrityResult Ok(int pages) => new(IntegrityStatus.Ok, pages, null);
    public static IntegrityResult Damaged(string error, int? pages = null) => new(IntegrityStatus.Damaged, pages, error);
    public static IntegrityResult Skip(string reason) => new(IntegrityStatus.Skipped, null, reason);
}

// Decides whether a single ebook file is healthy: it must open as a PDF or
// EPUB (converting via Calibre first when it is neither) and contain at least
// MinPages pages. PDF page counts are exact; EPUB pages are estimated from text
// length (see EpubInspector). Stateless and thread-safe — the scheduled
// BookIntegrityService calls CheckAsync once per file.
public sealed class BookIntegrityChecker
{
    // A book with fewer than this many pages is treated as damaged — short
    // enough to clear real pamphlets only rarely, long enough to catch the
    // truncated / placeholder / failed-download files this job exists to find.
    public const int MinPages = 20;

    // Extensions we consider "an ebook" and therefore in scope for the check.
    public static readonly IReadOnlySet<string> EbookExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "epub", "pdf", "mobi", "azw", "azw3", "fb2", "cbz", "cbr", "lit", "djvu", "doc", "docx", "rtf", "txt",
    };

    private readonly CalibreConverter _converter;
    private readonly IFileSystem _fs;
    private readonly ILogger<BookIntegrityChecker> _log;

    public BookIntegrityChecker(CalibreConverter converter, IFileSystem fs, ILogger<BookIntegrityChecker> log)
    {
        _converter = converter;
        _fs = fs;
        _log = log;
    }

    public static string ExtensionOf(string path)
        => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    public static bool IsEbook(string path) => EbookExtensions.Contains(ExtensionOf(path));

    public async Task<IntegrityResult> CheckAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || !_fs.FileExists(path))
            return IntegrityResult.Damaged("File no longer exists on disk.");

        var ext = ExtensionOf(path);
        try
        {
            return ext switch
            {
                "pdf" => CheckPdf(path),
                "epub" => CheckEpub(path),
                _ => await CheckViaConversionAsync(path, ct),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Any unexpected failure reading the file means it can't be opened
            // — exactly what "damaged" is meant to capture.
            _log.LogDebug(ex, "Integrity check threw for {Path}", path);
            return IntegrityResult.Damaged($"Could not open file: {ex.Message}");
        }
    }

    private IntegrityResult CheckPdf(string path)
    {
        int pageCount;
        try
        {
            using var doc = PdfDocument.Open(path);
            pageCount = doc.NumberOfPages;
        }
        catch (Exception ex)
        {
            return IntegrityResult.Damaged($"PDF could not be opened: {ex.Message}");
        }

        return pageCount < MinPages
            ? IntegrityResult.Damaged($"PDF has only {pageCount} page(s); at least {MinPages} required.", pageCount)
            : IntegrityResult.Ok(pageCount);
    }

    private IntegrityResult CheckEpub(string path)
    {
        using var stream = _fs.OpenRead(path);
        return EvaluateEpub(stream);
    }

    private async Task<IntegrityResult> CheckViaConversionAsync(string path, CancellationToken ct)
    {
        if (!_converter.IsConfigured)
            return IntegrityResult.Skip("Calibre ebook-convert is not configured; skipping non-EPUB/PDF format.");

        string epubPath;
        try
        {
            epubPath = await _converter.ConvertToEpubAsync(path, ct);
        }
        catch (CalibreConversionException ex)
        {
            return IntegrityResult.Damaged($"Could not convert to EPUB: {ex.Message}");
        }

        try
        {
            using var stream = _fs.OpenRead(epubPath);
            return EvaluateEpub(stream);
        }
        finally
        {
            try { if (_fs.FileExists(epubPath)) _fs.DeleteFile(epubPath); }
            catch (Exception ex) { _log.LogDebug(ex, "Failed to delete temp EPUB {Path}", epubPath); }
        }
    }

    private static IntegrityResult EvaluateEpub(Stream stream)
    {
        var inspection = EpubInspector.Inspect(stream);
        if (!inspection.Valid)
            return IntegrityResult.Damaged(inspection.Error ?? "EPUB is not valid.");
        return inspection.EstimatedPages < MinPages
            ? IntegrityResult.Damaged(
                $"EPUB has about {inspection.EstimatedPages} page(s) of text; at least {MinPages} required.",
                inspection.EstimatedPages)
            : IntegrityResult.Ok(inspection.EstimatedPages);
    }
}

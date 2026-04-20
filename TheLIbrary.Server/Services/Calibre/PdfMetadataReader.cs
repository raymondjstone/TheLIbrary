using UglyToad.PdfPig;

namespace TheLibrary.Server.Services.Calibre;

// PDF metadata is stored in the document Info dictionary (Title, Author,
// Subject, Keywords). Fields are often empty or wrong in practice — we
// return whatever the file has and let the caller fall back to the
// filename when both title and author are missing.
public static class PdfMetadataReader
{
    public static EpubMetadata? TryReadFile(string path, ILogger? log = null)
    {
        // PdfPig 0.1.x NREs deep inside its encryption handler on protected
        // PDFs instead of throwing PdfDocumentEncryptedException. The error
        // is handled by our catch but still noisy under a debugger, so skip
        // the Open entirely when the trailer advertises encryption.
        if (LooksEncrypted(path)) return null;

        PdfDocument? doc = null;
        try
        {
            doc = PdfDocument.Open(path);
            var info = doc.Information;
            var title = string.IsNullOrWhiteSpace(info?.Title) ? null : info!.Title.Trim();
            var author = string.IsNullOrWhiteSpace(info?.Author) ? null : info!.Author.Trim();
            if (title is null && author is null) return null;

            string? authorSort = author is not null && author.Contains(',') ? author : null;
            return new EpubMetadata(title, author, authorSort, null);
        }
        catch (Exception ex)
        {
            log?.LogDebug(ex, "PdfMetadataReader failed to read {Path}", path);
            return null;
        }
        finally
        {
            try { doc?.Dispose(); }
            catch (Exception ex) { log?.LogDebug(ex, "PdfMetadataReader dispose failed for {Path}", path); }
        }
    }

    // Cheap "is this encrypted?" probe. A PDF's trailer sits at the end of
    // the file; if the document is encrypted the trailer dictionary contains
    // an "/Encrypt" entry. Scan the tail of the file for that literal —
    // crude, but avoids a full parse and keeps us out of PdfPig's buggy
    // password path. Tail-only read so we don't slurp a 100 MB textbook.
    private static bool LooksEncrypted(string path)
    {
        const int tail = 8 * 1024;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var take = (int)Math.Min(fs.Length, tail);
            fs.Seek(-take, SeekOrigin.End);
            var buf = new byte[take];
            var read = fs.Read(buf, 0, take);
            // ASCII "/Encrypt"
            ReadOnlySpan<byte> needle = "/Encrypt"u8;
            var hay = buf.AsSpan(0, read);
            return hay.IndexOf(needle) >= 0;
        }
        catch
        {
            // If we can't probe the file, let the normal path try anyway
            // — a downstream error is caught and logged by the caller.
            return false;
        }
    }
}

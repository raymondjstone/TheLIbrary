using TheLibrary.Server.Services.IO;
using UglyToad.PdfPig;

namespace TheLibrary.Server.Services.Calibre;

// Returns the leading text of a book (front matter) for content-based
// identification, reusing the format-specific extractors. PDF and EPUB are read
// only as far as needed; MOBI/AZW/LIT go through Calibre. Image-only and binary
// formats yield "".
public sealed class BookTextReader
{
    private readonly IFileSystem _fs;
    private readonly CalibreConverter _converter;
    private readonly ILogger<BookTextReader> _log;

    public BookTextReader(IFileSystem fs, CalibreConverter converter, ILogger<BookTextReader> log)
    {
        _fs = fs;
        _converter = converter;
        _log = log;
    }

    // Returns the front matter AND the back matter of a book, joined together —
    // for content identification. "Also by / Novels by / Other books" lists and
    // series/contents listings live in the back of a book at least as often as
    // the front, so reading only the head misses most of them. Each end is capped
    // at maxChars; on a book shorter than that the two overlap harmlessly.
    public async Task<string> ReadHeadAndTailAsync(string path, int maxChars, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || !_fs.FileExists(path)) return "";
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        try
        {
            switch (ext)
            {
                case "pdf": return ReadPdfHeadAndTail(path, maxChars);
                case "epub":
                {
                    using var s = _fs.OpenRead(path);
                    var (head, tail) = EpubInspector.ReadHeadAndTailText(s, maxChars);
                    return Combine(head, tail);
                }
                case "fb2":
                {
                    using var s = _fs.OpenRead(path);
                    return HeadAndTail(Fb2TextExtractor.ExtractText(s), maxChars);
                }
                case "docx":
                {
                    using var s = _fs.OpenRead(path);
                    return HeadAndTail(OfficeTextExtractor.ExtractDocx(s), maxChars);
                }
                case "odt":
                {
                    using var s = _fs.OpenRead(path);
                    return HeadAndTail(OfficeTextExtractor.ExtractOdt(s), maxChars);
                }
                case "rtf":
                {
                    using var s = _fs.OpenRead(path);
                    return HeadAndTail(RtfTextExtractor.ExtractText(s), maxChars);
                }
                case "txt": return Combine(await ReadTxtHeadAsync(path, maxChars, ct), ReadTxtTail(path, maxChars));
                case "mobi" or "azw" or "azw3" or "lit":
                    return await ReadHeadAndTailViaConversionAsync(path, maxChars, ct);
                default:
                    return "";
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "BookTextReader (head+tail) failed for {Path}", path);
            return "";
        }
    }

    public async Task<string> ReadHeadAsync(string path, int maxChars, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || !_fs.FileExists(path)) return "";
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        try
        {
            switch (ext)
            {
                case "pdf": return ReadPdfHead(path, maxChars);
                case "epub":
                {
                    using var s = _fs.OpenRead(path);
                    return EpubInspector.ReadHeadText(s, maxChars);
                }
                case "fb2":
                {
                    using var s = _fs.OpenRead(path);
                    return Head(Fb2TextExtractor.ExtractText(s), maxChars);
                }
                case "docx":
                {
                    using var s = _fs.OpenRead(path);
                    return Head(OfficeTextExtractor.ExtractDocx(s), maxChars);
                }
                case "odt":
                {
                    using var s = _fs.OpenRead(path);
                    return Head(OfficeTextExtractor.ExtractOdt(s), maxChars);
                }
                case "rtf":
                {
                    using var s = _fs.OpenRead(path);
                    return Head(RtfTextExtractor.ExtractText(s), maxChars);
                }
                case "txt":
                {
                    using var s = _fs.OpenRead(path);
                    using var reader = new StreamReader(s);
                    var buf = new char[maxChars];
                    var n = await reader.ReadBlockAsync(buf, 0, maxChars);
                    return new string(buf, 0, n);
                }
                case "mobi" or "azw" or "azw3" or "lit":
                    return await ReadViaConversionAsync(path, maxChars, ct);
                default:
                    return ""; // cbz/cbr (images), djvu, doc (legacy binary) — no text
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "BookTextReader failed for {Path}", path);
            return "";
        }
    }

    private string ReadPdfHead(string path, int maxChars)
    {
        try
        {
            using var doc = PdfDocument.Open(path);
            var sb = new System.Text.StringBuilder();
            foreach (var page in doc.GetPages())
            {
                sb.Append(page.Text).Append('\n');
                if (sb.Length >= maxChars) break;
            }
            return Head(sb.ToString(), maxChars);
        }
        catch { return ""; }
    }

    // Head and tail in one open — a PDF on the NAS mount shouldn't be parsed
    // twice just to read both ends.
    private string ReadPdfHeadAndTail(string path, int maxChars)
    {
        try
        {
            using var doc = PdfDocument.Open(path);

            var hb = new System.Text.StringBuilder();
            foreach (var page in doc.GetPages())
            {
                hb.Append(page.Text).Append('\n');
                if (hb.Length >= maxChars) break;
            }
            var head = Head(hb.ToString(), maxChars);

            var tb = new System.Text.StringBuilder();
            // Walk pages from the last backward until the budget fills.
            for (var n = doc.NumberOfPages; n >= 1 && tb.Length < maxChars; n--)
                tb.Insert(0, doc.GetPage(n).Text + "\n");
            var t = tb.ToString();
            var tail = t.Length > maxChars ? t[^maxChars..] : t;

            return Combine(head, tail);
        }
        catch { return ""; }
    }

    private async Task<string> ReadTxtHeadAsync(string path, int maxChars, CancellationToken ct)
    {
        using var s = _fs.OpenRead(path);
        using var reader = new StreamReader(s);
        var buf = new char[maxChars];
        var n = await reader.ReadBlockAsync(buf, 0, maxChars);
        return new string(buf, 0, n);
    }

    // Reads the last ~maxChars of a text file by seeking near the end, so a huge
    // .txt isn't read in full just to see its back matter. Byte-approximate (UTF-8
    // multi-byte chars at the cut point are tolerated) — good enough for heuristics.
    private string ReadTxtTail(string path, int maxChars)
    {
        try
        {
            using var s = _fs.OpenRead(path);
            if (!s.CanSeek) return "";
            var approxBytes = (long)maxChars * 2; // headroom for multi-byte chars
            if (s.Length > approxBytes) s.Seek(-approxBytes, SeekOrigin.End);
            using var reader = new StreamReader(s);
            var text = reader.ReadToEnd();
            return text.Length > maxChars ? text[^maxChars..] : text;
        }
        catch { return ""; }
    }

    // Head+tail for the formats that need a full extraction anyway (FB2/DOCX/ODT/
    // RTF): take the leading and trailing slices of the one extracted string.
    private static string HeadAndTail(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.Length <= maxChars) return text;
        var head = text[..maxChars];
        var tail = text[^maxChars..];
        return Combine(head, tail);
    }

    private async Task<string> ReadHeadAndTailViaConversionAsync(string path, int maxChars, CancellationToken ct)
    {
        if (!_converter.IsConfigured) return "";
        string epub;
        try { epub = await _converter.ConvertToEpubAsync(path, ct); }
        catch (CalibreConversionException) { return ""; }
        try
        {
            using var s = _fs.OpenRead(epub);
            var (head, tail) = EpubInspector.ReadHeadAndTailText(s, maxChars);
            return Combine(head, tail);
        }
        finally
        {
            try { if (_fs.FileExists(epub)) _fs.DeleteFile(epub); } catch { /* best effort */ }
        }
    }

    // Joins front and back matter. Drops the tail when it's empty or already
    // contained in the head (short books read end-to-end), so we never feed the
    // parser the same list twice.
    private static string Combine(string head, string tail)
    {
        if (string.IsNullOrEmpty(tail) || head.Contains(tail, StringComparison.Ordinal)) return head;
        if (string.IsNullOrEmpty(head)) return tail;
        return head + "\n\n" + tail;
    }

    private async Task<string> ReadViaConversionAsync(string path, int maxChars, CancellationToken ct)
    {
        if (!_converter.IsConfigured) return "";
        string epub;
        try { epub = await _converter.ConvertToEpubAsync(path, ct); }
        catch (CalibreConversionException) { return ""; }
        try
        {
            using var s = _fs.OpenRead(epub);
            return EpubInspector.ReadHeadText(s, maxChars);
        }
        finally
        {
            try { if (_fs.FileExists(epub)) _fs.DeleteFile(epub); } catch { /* best effort */ }
        }
    }

    private static string Head(string text, int maxChars)
        => string.IsNullOrEmpty(text) || text.Length <= maxChars ? text : text[..maxChars];
}

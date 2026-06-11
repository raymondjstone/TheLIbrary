using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TheLibrary.Server.Services.Calibre;

public sealed record EpubInspection(bool Valid, int TextChars, int EstimatedPages, string? Error);

// Validates that a stream is a structurally-sound EPUB and estimates its page
// count from the amount of readable text. EPUB has no native page concept, so
// we approximate the way most readers do: total visible-text characters divided
// by a fixed chars-per-page figure. Pure and stream-based so it unit-tests with
// an in-memory zip and works equally on a real file or a Calibre-converted temp.
public static class EpubInspector
{
    // Roughly one printed page of prose. Adobe/Calibre use ~1024 chars per
    // "page"; we match that so the 20-page floor lines up with expectations.
    public const int CharsPerPage = 1024;

    private static readonly XNamespace ContainerNs = "urn:oasis:names:tc:opendocument:xmlns:container";
    private static readonly Regex TagRx = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRx = new(@"\s+", RegexOptions.Compiled);
    // For ReadHeadText: turn block ends into newlines, collapse intra-line space.
    private static readonly Regex BlockEndRx = new(
        @"</(p|div|h[1-6]|li|tr)\s*>|<br\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex InlineWsRx = new(@"[^\S\n]+", RegexOptions.Compiled);
    private static readonly Regex TrimLineWsRx = new(@" *\n *", RegexOptions.Compiled);

    // Returns up to maxChars of leading readable text in spine order (front
    // matter first) — for content-based identification. Best-effort: returns ""
    // on any parse problem.
    public static string ReadHeadText(Stream stream, int maxChars)
    {
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var docs = ResolveSpineDocuments(zip);

            var sb = new StringBuilder();
            foreach (var entry in docs)
            {
                if (sb.Length >= maxChars) break;
                var text = ReadDocText(entry);
                if (text.Length > 0) sb.Append(text).Append('\n');
            }
            return sb.Length > maxChars ? sb.ToString(0, maxChars) : sb.ToString();
        }
        catch { return ""; }
    }

    // Head and tail in one pass: the zip is opened and the spine resolved once,
    // and each content document is decompressed at most once (short books would
    // otherwise be read twice over). Equivalent to calling ReadHeadText and
    // ReadTailText on two separate streams, at half the I/O.
    public static (string Head, string Tail) ReadHeadAndTailText(Stream stream, int maxChars)
    {
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var docs = ResolveSpineDocuments(zip);
            var texts = new string?[docs.Count];
            string TextOf(int i) => texts[i] ??= ReadDocText(docs[i]);

            var sb = new StringBuilder();
            for (var i = 0; i < docs.Count && sb.Length < maxChars; i++)
            {
                var text = TextOf(i);
                if (text.Length > 0) sb.Append(text).Append('\n');
            }
            var head = sb.Length > maxChars ? sb.ToString(0, maxChars) : sb.ToString();

            var collected = new List<string>();
            var total = 0;
            for (var i = docs.Count - 1; i >= 0 && total < maxChars; i--)
            {
                var text = TextOf(i);
                if (text.Length == 0) continue;
                collected.Add(text);
                total += text.Length + 1;
            }
            collected.Reverse(); // back to reading order
            var tb = new StringBuilder();
            foreach (var t in collected) tb.Append(t).Append('\n');
            // Keep the *last* maxChars so the tail end (where the list sits) wins.
            var tail = tb.Length > maxChars ? tb.ToString(tb.Length - maxChars, maxChars) : tb.ToString();

            return (head, tail);
        }
        catch { return ("", ""); }
    }

    // Returns up to maxChars of *trailing* readable text in spine order — the
    // back matter, where "Also by / Novels by / Other books" bibliographies and
    // series listings most often live. Reads spine documents from the end until
    // the budget fills, then returns them in natural reading order so line-based
    // heuristics see the list the right way up. Best-effort: "" on any problem.
    public static string ReadTailText(Stream stream, int maxChars)
    {
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var docs = ResolveSpineDocuments(zip);

            var collected = new List<string>();
            var total = 0;
            for (var i = docs.Count - 1; i >= 0 && total < maxChars; i--)
            {
                var text = ReadDocText(docs[i]);
                if (text.Length == 0) continue;
                collected.Add(text);
                total += text.Length + 1;
            }
            collected.Reverse(); // back to reading order
            var sb = new StringBuilder();
            foreach (var t in collected) sb.Append(t).Append('\n');
            // Keep the *last* maxChars so the tail end (where the list sits) wins.
            return sb.Length > maxChars ? sb.ToString(sb.Length - maxChars, maxChars) : sb.ToString();
        }
        catch { return ""; }
    }

    // Resolves the spine's content documents in reading order, reusing the same
    // robust href resolution as Inspect and falling back to every content
    // document when the spine can't be resolved.
    private static List<ZipArchiveEntry> ResolveSpineDocuments(ZipArchive zip)
    {
        var container = zip.GetEntry("META-INF/container.xml");
        if (container is null) return new List<ZipArchiveEntry>();
        string opfPath;
        using (var cs = container.Open())
            opfPath = XDocument.Load(cs).Descendants(ContainerNs + "rootfile")
                .FirstOrDefault()?.Attribute("full-path")?.Value ?? "";
        if (string.IsNullOrWhiteSpace(opfPath)) return new List<ZipArchiveEntry>();
        var opf = zip.GetEntry(opfPath);
        if (opf is null) return new List<ZipArchiveEntry>();

        XDocument odoc;
        using (var os = opf.Open()) odoc = XDocument.Load(os);
        var manifest = odoc.Descendants().Where(e => e.Name.LocalName == "item")
            .Where(e => e.Attribute("id") is not null && e.Attribute("href") is not null)
            .GroupBy(e => e.Attribute("id")!.Value)
            .ToDictionary(g => g.Key, g => g.First().Attribute("href")!.Value);
        var spine = odoc.Descendants().Where(e => e.Name.LocalName == "itemref")
            .Select(e => e.Attribute("idref")?.Value).Where(id => id is not null).Select(id => id!).ToList();
        var opfDir = opfPath.Contains('/') ? opfPath[..opfPath.LastIndexOf('/')] : "";

        var docs = new List<ZipArchiveEntry>();
        foreach (var idref in spine)
        {
            if (!manifest.TryGetValue(idref, out var href)) continue;
            var entry = ResolveEntry(zip, opfDir, href);
            if (entry is not null) docs.Add(entry);
        }
        if (docs.Count == 0)
            docs = zip.Entries.Where(e => IsContentDocument(e.FullName))
                .OrderBy(e => e.FullName, StringComparer.Ordinal).ToList();
        return docs;
    }

    // Reads one content document and returns its readable text with paragraph /
    // line breaks preserved as newlines (so line-based front-matter heuristics —
    // Gutenberg headers, "also by" lists — still work).
    private static string ReadDocText(ZipArchiveEntry entry)
    {
        using var es = entry.Open();
        using var reader = new StreamReader(es);
        var html = reader.ReadToEnd();
        var withBreaks = BlockEndRx.Replace(html, "\n");
        var text = System.Net.WebUtility.HtmlDecode(TagRx.Replace(withBreaks, " "));
        text = InlineWsRx.Replace(text, " ");
        return TrimLineWsRx.Replace(text, "\n").Trim();
    }

    public static EpubInspection Inspect(Stream stream)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (Exception ex)
        {
            return new EpubInspection(false, 0, 0, $"Not a valid EPUB (zip) file: {ex.Message}");
        }

        using (zip)
        {
            try
            {
                var container = zip.GetEntry("META-INF/container.xml");
                if (container is null)
                    return new EpubInspection(false, 0, 0, "EPUB is missing META-INF/container.xml.");

                string opfPath;
                using (var cs = container.Open())
                {
                    var cdoc = XDocument.Load(cs);
                    opfPath = cdoc.Descendants(ContainerNs + "rootfile")
                        .FirstOrDefault()?.Attribute("full-path")?.Value ?? "";
                }
                if (string.IsNullOrWhiteSpace(opfPath))
                    return new EpubInspection(false, 0, 0, "EPUB container.xml has no rootfile path.");

                var opf = zip.GetEntry(opfPath);
                if (opf is null)
                    return new EpubInspection(false, 0, 0, $"EPUB OPF package '{opfPath}' not found.");

                XDocument odoc;
                using (var os = opf.Open()) odoc = XDocument.Load(os);

                // Build manifest (id -> href) and the spine reading order (idrefs).
                // LocalName comparisons keep us namespace-agnostic across the
                // OPF 2/3 variants seen in the wild.
                var manifest = odoc.Descendants()
                    .Where(e => e.Name.LocalName == "item")
                    .Where(e => e.Attribute("id") is not null && e.Attribute("href") is not null)
                    .GroupBy(e => e.Attribute("id")!.Value)
                    .ToDictionary(g => g.Key, g => g.First().Attribute("href")!.Value);

                var spine = odoc.Descendants()
                    .Where(e => e.Name.LocalName == "itemref")
                    .Select(e => e.Attribute("idref")?.Value)
                    .Where(id => id is not null)
                    .Select(id => id!)
                    .ToList();

                if (spine.Count == 0)
                    return new EpubInspection(false, 0, 0, "EPUB spine is empty (no reading order).");

                // OPF hrefs are relative to the OPF's own directory.
                var opfDir = opfPath.Contains('/') ? opfPath[..opfPath.LastIndexOf('/')] : "";

                // Resolve the spine's content documents. Manifest hrefs are
                // URL-encoded and relative to the OPF, and zip lookups are
                // case-sensitive — all of which can make a naive GetEntry miss
                // every document and report a real book as "0 pages". Resolution
                // therefore tries several candidate paths per href.
                var contentEntries = new List<ZipArchiveEntry>();
                foreach (var idref in spine)
                {
                    if (!manifest.TryGetValue(idref, out var href)) continue;
                    var entry = ResolveEntry(zip, opfDir, href);
                    if (entry is not null) contentEntries.Add(entry);
                }

                // Fallback: if not one spine document resolved (encoding/path
                // quirks), tally every content document in the archive instead
                // so a perfectly readable book is never scored as empty.
                if (contentEntries.Count == 0)
                    contentEntries = zip.Entries
                        .Where(e => IsContentDocument(e.FullName))
                        .OrderBy(e => e.FullName, StringComparer.Ordinal)
                        .ToList();

                long totalChars = 0;
                foreach (var entry in contentEntries)
                {
                    using var es = entry.Open();
                    using var reader = new StreamReader(es);
                    totalChars += CountVisibleText(reader.ReadToEnd());
                }

                var chars = (int)Math.Min(int.MaxValue, totalChars);
                var pages = chars / CharsPerPage;
                return new EpubInspection(true, chars, pages, null);
            }
            catch (Exception ex)
            {
                return new EpubInspection(false, 0, 0, $"EPUB could not be parsed: {ex.Message}");
            }
        }
    }

    // Locates the zip entry for a manifest href, tolerating URL-encoding, the
    // OPF-relative base directory, in-document fragments, and case differences.
    private static ZipArchiveEntry? ResolveEntry(ZipArchive zip, string opfDir, string href)
    {
        href = href.Split('#')[0]; // drop any in-document fragment
        if (string.IsNullOrWhiteSpace(href)) return null;
        var decoded = SafeUnescape(href);

        // Exact lookups first (cheap), encoded and decoded, with/without base dir.
        foreach (var candidate in new[]
        {
            CombineZipPath(opfDir, href),
            CombineZipPath(opfDir, decoded),
            href,
            decoded,
        })
        {
            var entry = zip.GetEntry(candidate);
            if (entry is not null) return entry;
        }

        // Case-insensitive full-path match (some EPUBs differ only by case).
        var wanted = CombineZipPath(opfDir, decoded);
        var ci = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, wanted, StringComparison.OrdinalIgnoreCase));
        if (ci is not null) return ci;

        // Last resort: match by leaf filename.
        var leaf = decoded.Contains('/') ? decoded[(decoded.LastIndexOf('/') + 1)..] : decoded;
        return zip.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith("/" + leaf, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.FullName, leaf, StringComparison.OrdinalIgnoreCase));
    }

    private static string SafeUnescape(string s)
    {
        try { return Uri.UnescapeDataString(s); }
        catch { return s; }
    }

    private static bool IsContentDocument(string name)
    {
        var n = name.ToLowerInvariant();
        return n.EndsWith(".xhtml") || n.EndsWith(".html") || n.EndsWith(".htm");
    }

    // Strips markup and collapses whitespace, returning the count of remaining
    // characters — a stand-in for "how much a reader would actually see".
    private static int CountVisibleText(string html)
    {
        if (string.IsNullOrEmpty(html)) return 0;
        var noTags = TagRx.Replace(html, " ");
        var collapsed = WhitespaceRx.Replace(noTags, " ").Trim();
        return collapsed.Length;
    }

    // Resolves a spine href against the OPF directory, flattening any leading
    // "./" or "../" segments to a clean zip-entry path (entries use '/').
    private static string CombineZipPath(string baseDir, string href)
    {
        href = href.Split('#')[0]; // drop any in-document fragment
        var segments = new List<string>();
        if (!string.IsNullOrEmpty(baseDir))
            segments.AddRange(baseDir.Split('/', StringSplitOptions.RemoveEmptyEntries));

        foreach (var seg in href.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); }
            else segments.Add(seg);
        }
        return string.Join('/', segments);
    }
}

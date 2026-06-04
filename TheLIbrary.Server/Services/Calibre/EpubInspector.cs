using System.IO.Compression;
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

                long totalChars = 0;
                foreach (var idref in spine)
                {
                    if (!manifest.TryGetValue(idref, out var href)) continue;
                    var entryPath = CombineZipPath(opfDir, href);
                    var entry = zip.GetEntry(entryPath) ?? zip.GetEntry(href);
                    if (entry is null) continue;

                    using var es = entry.Open();
                    using var reader = new StreamReader(es);
                    var html = reader.ReadToEnd();
                    totalChars += CountVisibleText(html);
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

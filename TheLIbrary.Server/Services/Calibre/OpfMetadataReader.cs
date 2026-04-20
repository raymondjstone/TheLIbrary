using System.Xml.Linq;

namespace TheLibrary.Server.Services.Calibre;

// Standalone OPF file reader — Calibre writes a "metadata.opf" next to each
// book containing the same Dublin Core metadata that lives inside an EPUB's
// OPF. We reuse the DC / OPF namespaces and look for <dc:title>, <dc:creator>
// (with opf:file-as for author_sort), and <dc:language>.
public static class OpfMetadataReader
{
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";

    public static EpubMetadata? TryReadFile(string opfPath)
    {
        try
        {
            using var fs = File.OpenRead(opfPath);
            var doc = XDocument.Load(fs);
            var meta = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "metadata");
            if (meta is null) return null;

            var creator = meta.Elements(Dc + "creator").FirstOrDefault();
            var authorSort = creator?.Attribute(Opf + "file-as")?.Value;

            return new EpubMetadata(
                Trim(meta.Elements(Dc + "title").FirstOrDefault()?.Value),
                Trim(creator?.Value),
                Trim(authorSort),
                Trim(meta.Elements(Dc + "language").FirstOrDefault()?.Value));
        }
        catch { return null; }
    }

    private static string? Trim(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

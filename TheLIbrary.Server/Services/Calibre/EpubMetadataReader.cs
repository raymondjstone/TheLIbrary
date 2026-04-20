using System.IO.Compression;
using System.Xml.Linq;

namespace TheLibrary.Server.Services.Calibre;

public sealed record EpubMetadata(string? Title, string? Author, string? AuthorSort, string? Language);

// Reads Dublin Core metadata out of an .epub file. EPUB is a zip whose
// META-INF/container.xml points at an OPF file containing
// <metadata><dc:title/><dc:creator opf:file-as=""/><dc:language/></metadata>.
// We only need those values and it's cheap enough to do with the BCL alone.
public static class EpubMetadataReader
{
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace ContainerNs = "urn:oasis:names:tc:opendocument:xmlns:container";

    // Reads the first .epub found in the folder.
    public static EpubMetadata? TryRead(string folderPath)
    {
        string? epub;
        try { epub = Directory.EnumerateFiles(folderPath, "*.epub").FirstOrDefault(); }
        catch { return null; }
        return epub is null ? null : TryReadFile(epub);
    }

    public static EpubMetadata? TryReadFile(string epubPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(epubPath);

            var container = zip.GetEntry("META-INF/container.xml");
            if (container is null) return null;

            string opfPath;
            using (var cs = container.Open())
            {
                var cdoc = XDocument.Load(cs);
                opfPath = cdoc.Descendants(ContainerNs + "rootfile")
                    .FirstOrDefault()?.Attribute("full-path")?.Value ?? "";
            }
            if (string.IsNullOrWhiteSpace(opfPath)) return null;

            var opf = zip.GetEntry(opfPath);
            if (opf is null) return null;

            using var os = opf.Open();
            var odoc = XDocument.Load(os);
            var meta = odoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "metadata");
            if (meta is null) return null;

            var creator = meta.Elements(Dc + "creator").FirstOrDefault();
            var authorSort = creator?.Attribute(Opf + "file-as")?.Value;

            return new EpubMetadata(
                Trim(meta.Elements(Dc + "title").FirstOrDefault()?.Value),
                Trim(creator?.Value),
                Trim(authorSort),
                Trim(meta.Elements(Dc + "language").FirstOrDefault()?.Value));
        }
        catch
        {
            return null;
        }
    }

    private static string? Trim(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

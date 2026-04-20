using System.IO.Compression;
using System.Xml.Linq;

namespace TheLibrary.Server.Services.Calibre;

// .docx / .odt are both zip containers exposing Dublin Core metadata.
//   DOCX: docProps/core.xml with cp:coreProperties + dc:title / dc:creator / dc:language
//   ODT : meta.xml with office:meta containing dc:title / dc:creator / dc:language
// Same shape in both cases — match by local names to dodge namespace differences.
public static class DocxMetadataReader
{
    public static EpubMetadata? TryReadFile(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry =
                zip.GetEntry("docProps/core.xml") ??
                zip.GetEntry("meta.xml");
            if (entry is null) return null;

            using var s = entry.Open();
            var doc = XDocument.Load(s);

            string? Pick(string local) => doc.Descendants()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName, local, StringComparison.OrdinalIgnoreCase))
                ?.Value?.Trim();

            var title = Pick("title");
            var author = Pick("creator");
            var lang = Pick("language");

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(author))
                return null;

            return new EpubMetadata(
                string.IsNullOrWhiteSpace(title) ? null : title,
                string.IsNullOrWhiteSpace(author) ? null : author,
                null,
                string.IsNullOrWhiteSpace(lang) ? null : lang);
        }
        catch { return null; }
    }
}

using System.IO.Compression;
using System.Xml.Linq;

namespace TheLibrary.Server.Services.Calibre;

// Comic Book ZIP (.cbz) is a zip of page images with an optional ComicInfo.xml
// at the root describing the series/issue. Anansi Project schema:
//   <ComicInfo><Title/><Series/><Writer/><Publisher/><LanguageISO/></ComicInfo>
// Writer is a comma-separated list; take the first entry as the primary author.
public static class CbzMetadataReader
{
    public static EpubMetadata? TryReadFile(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.Entries.FirstOrDefault(e =>
                string.Equals(e.FullName, "ComicInfo.xml", StringComparison.OrdinalIgnoreCase));
            if (entry is null) return null;

            using var s = entry.Open();
            var doc = XDocument.Load(s);
            var root = doc.Root;
            if (root is null) return null;

            string? Pick(string local) => root.Elements()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName, local, StringComparison.OrdinalIgnoreCase))
                ?.Value?.Trim();

            var title = Pick("Title");
            var series = Pick("Series");
            var writer = Pick("Writer");
            var lang = Pick("LanguageISO");

            var author = writer?.Split(',').Select(p => p.Trim())
                .FirstOrDefault(p => !string.IsNullOrEmpty(p));

            // When a comic has only a series, use that as the title — it at
            // least keeps issues in the same series folder.
            var effectiveTitle = string.IsNullOrWhiteSpace(title) ? series : title;

            if (string.IsNullOrWhiteSpace(effectiveTitle) && string.IsNullOrWhiteSpace(author))
                return null;

            return new EpubMetadata(
                string.IsNullOrWhiteSpace(effectiveTitle) ? null : effectiveTitle,
                string.IsNullOrWhiteSpace(author) ? null : author,
                null,
                string.IsNullOrWhiteSpace(lang) ? null : lang);
        }
        catch { return null; }
    }
}

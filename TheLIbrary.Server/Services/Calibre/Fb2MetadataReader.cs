using System.IO.Compression;
using System.Xml.Linq;

namespace TheLibrary.Server.Services.Calibre;

// FictionBook (.fb2) files are plain XML. The .fb2.zip / .fbz variants wrap
// a single .fb2 inside a zip. Metadata lives under FictionBook/description/
// title-info with a fixed namespace.
public static class Fb2MetadataReader
{
    private static readonly XNamespace Ns = "http://www.gribuser.ru/xml/fictionbook/2.0";

    public static EpubMetadata? TryReadFile(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".fb2")
            {
                using var fs = File.OpenRead(path);
                return Parse(XDocument.Load(fs));
            }
            if (ext == ".zip" || ext == ".fbz" || ext == ".fb2.zip"
                || path.EndsWith(".fb2.zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(path);
                var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".fb2",
                    StringComparison.OrdinalIgnoreCase));
                if (entry is null) return null;
                using var es = entry.Open();
                return Parse(XDocument.Load(es));
            }
            return null;
        }
        catch { return null; }
    }

    private static EpubMetadata Parse(XDocument doc)
    {
        // Some files use the default namespace, others don't — match by local name
        // to stay resilient.
        var titleInfo = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "title-info");
        string? title = titleInfo?.Elements().FirstOrDefault(e => e.Name.LocalName == "book-title")?.Value;
        string? lang = titleInfo?.Elements().FirstOrDefault(e => e.Name.LocalName == "lang")?.Value;

        string? author = null;
        string? authorSort = null;
        var authorEl = titleInfo?.Elements().FirstOrDefault(e => e.Name.LocalName == "author");
        if (authorEl is not null)
        {
            string? First(string local) => authorEl.Elements()
                .FirstOrDefault(e => e.Name.LocalName == local)?.Value?.Trim();
            var first = First("first-name");
            var middle = First("middle-name");
            var last = First("last-name");
            var nickname = First("nickname");
            var parts = new[] { first, middle, last }.Where(s => !string.IsNullOrWhiteSpace(s));
            author = string.Join(" ", parts).Trim();
            if (string.IsNullOrWhiteSpace(author)) author = nickname;
            if (!string.IsNullOrWhiteSpace(last))
                authorSort = string.IsNullOrWhiteSpace(first) ? last : $"{last}, {first}";
        }

        return new EpubMetadata(
            string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            string.IsNullOrWhiteSpace(author) ? null : author,
            authorSort,
            string.IsNullOrWhiteSpace(lang) ? null : lang.Trim());
    }
}

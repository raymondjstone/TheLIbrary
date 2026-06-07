using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace TheLibrary.Server.Services.Calibre;

// Extracts readable text from the two zip-of-XML office formats with the BCL —
// no Calibre needed.
//   DOCX: word/document.xml, text in <w:t> runs, paragraphs <w:p>
//   ODT : content.xml, text in <text:p> / <text:h>
// Both use LocalName matching to dodge namespace prefixes.
public static class OfficeTextExtractor
{
    public static string ExtractDocx(Stream stream)
    {
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var entry = zip.GetEntry("word/document.xml");
            if (entry is null) return "";
            using var es = entry.Open();
            var doc = XDocument.Load(es);

            var sb = new StringBuilder();
            foreach (var p in doc.Descendants().Where(e => e.Name.LocalName == "p"))
            {
                foreach (var n in p.Descendants().Where(e => e.Name.LocalName is "t" or "tab" or "br"))
                {
                    if (n.Name.LocalName == "t") sb.Append(n.Value);
                    else if (n.Name.LocalName == "tab") sb.Append('\t');
                    else sb.Append(' ');
                }
                sb.Append('\n');
            }
            return sb.ToString().Trim();
        }
        catch { return ""; }
    }

    public static string ExtractOdt(Stream stream)
    {
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var entry = zip.GetEntry("content.xml");
            if (entry is null) return "";
            using var es = entry.Open();
            var doc = XDocument.Load(es);

            var sb = new StringBuilder();
            // text:p / text:h carry the visible text; .Value concatenates the
            // child spans, which is all we need for a length-based page estimate.
            foreach (var p in doc.Descendants().Where(e => e.Name.LocalName is "p" or "h"))
            {
                var t = p.Value;
                if (!string.IsNullOrEmpty(t)) sb.Append(t);
                sb.Append('\n');
            }
            return sb.ToString().Trim();
        }
        catch { return ""; }
    }
}

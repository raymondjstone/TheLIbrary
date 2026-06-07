using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace TheLibrary.Server.Services.Calibre;

// FictionBook (.fb2) is plain XML; .fbz / .fb2.zip wrap one .fb2 in a zip.
// Extracts the readable body text with the BCL — no Calibre needed. Mirrors the
// native parsing already used by Fb2MetadataReader.
public static class Fb2TextExtractor
{
    public static string ExtractText(Stream stream)
    {
        byte[] bytes;
        using (var ms = new MemoryStream()) { stream.CopyTo(ms); bytes = ms.ToArray(); }
        if (bytes.Length == 0) return "";

        // Zip local-file signature "PK\x03\x04" → it's a .fbz / .fb2.zip.
        if (bytes.Length >= 2 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K')
        {
            try
            {
                using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".fb2", StringComparison.OrdinalIgnoreCase))
                            ?? zip.Entries.FirstOrDefault();
                if (entry is null) return "";
                using var es = entry.Open();
                return ParseBodyText(es);
            }
            catch { return ""; }
        }

        try { return ParseBodyText(new MemoryStream(bytes)); }
        catch { return ""; }
    }

    private static string ParseBodyText(Stream xml)
    {
        var doc = XDocument.Load(xml);
        var sb = new StringBuilder();
        // LocalName matching dodges the FictionBook namespace. Skip the notes
        // body (it carries footnotes, not the main text) when a main body exists.
        var bodies = doc.Descendants().Where(e => e.Name.LocalName == "body").ToList();
        var mainBodies = bodies.Count > 1
            ? bodies.Where(b => !string.Equals((string?)b.Attribute("name"), "notes", StringComparison.OrdinalIgnoreCase)).ToList()
            : bodies;
        foreach (var body in (mainBodies.Count > 0 ? mainBodies : bodies))
        {
            foreach (var p in body.Descendants().Where(e => e.Name.LocalName is "p" or "v" or "subtitle"))
            {
                var t = p.Value.Trim();
                if (t.Length > 0) sb.Append(t).Append('\n');
            }
        }
        return sb.ToString().Trim();
    }
}

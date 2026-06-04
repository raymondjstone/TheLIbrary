using System.IO.Compression;
using System.Text;

namespace TheLibrary.Server.Tests.Infrastructure;

// Builds minimal but structurally-valid EPUB byte arrays for tests: a
// container.xml pointing at an OPF whose manifest + spine list the supplied
// content documents (stored under OEBPS/). Enough for EpubInspector to walk the
// spine and tally text.
internal static class TestEpub
{
    public static byte[] Build(params (string Href, string Html)[] docs)
        => Build(includeContainer: true, includeSpine: true, docs);

    public static byte[] Build(bool includeContainer, bool includeSpine, params (string Href, string Html)[] docs)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "mimetype", "application/epub+zip");

            if (includeContainer)
                Write(zip, "META-INF/container.xml", """
                    <?xml version="1.0"?>
                    <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                      <rootfiles>
                        <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
                      </rootfiles>
                    </container>
                    """);

            var manifest = new StringBuilder();
            var spine = new StringBuilder();
            for (var i = 0; i < docs.Length; i++)
            {
                var id = $"c{i}";
                manifest.Append($"<item id=\"{id}\" href=\"{docs[i].Href}\" media-type=\"application/xhtml+xml\"/>");
                if (includeSpine) spine.Append($"<itemref idref=\"{id}\"/>");
                Write(zip, $"OEBPS/{docs[i].Href}", docs[i].Html);
            }

            Write(zip, "OEBPS/content.opf", $"""
                <?xml version="1.0"?>
                <package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="id">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Test</dc:title></metadata>
                  <manifest>{manifest}</manifest>
                  <spine>{spine}</spine>
                </package>
                """);
        }
        return ms.ToArray();
    }

    // An XHTML document whose visible text is exactly `chars` characters long.
    public static string HtmlWithText(int chars)
        => $"<html><body><p>{new string('a', chars)}</p></body></html>";

    private static void Write(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path);
        using var s = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        s.Write(bytes, 0, bytes.Length);
    }
}

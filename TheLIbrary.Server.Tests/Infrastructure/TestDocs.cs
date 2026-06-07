using System.IO.Compression;
using System.Text;

namespace TheLibrary.Server.Tests.Infrastructure;

// Minimal valid FB2 / DOCX / ODT / CBZ byte arrays for exercising the native
// (Calibre-free) extractors and the integrity check.
internal static class TestDocs
{
    public static byte[] Fb2(int chars, int paragraphs = 1)
    {
        var body = string.Concat(Enumerable.Repeat($"<p>{new string('a', chars)}</p>", paragraphs));
        var xml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<FictionBook xmlns=""http://www.gribuser.ru/xml/fictionbook/2.0"">
<description><title-info><book-title>T</book-title></title-info></description>
<body>{body}</body></FictionBook>";
        return Encoding.UTF8.GetBytes(xml);
    }

    public static byte[] Docx(int chars) => Zip("word/document.xml", $@"<?xml version=""1.0""?>
<w:document xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main"">
<w:body><w:p><w:r><w:t>{new string('a', chars)}</w:t></w:r></w:p></w:body></w:document>");

    public static byte[] Odt(int chars) => Zip("content.xml", $@"<?xml version=""1.0""?>
<office:document-content
    xmlns:office=""urn:oasis:names:tc:opendocument:xmlns:office:1.0""
    xmlns:text=""urn:oasis:names:tc:opendocument:xmlns:text:1.0"">
<office:body><office:text><text:p>{new string('a', chars)}</text:p></office:text></office:body></office:document-content>");

    public static byte[] Cbz(int images)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < images; i++)
            {
                var e = zip.CreateEntry($"{i:000}.jpg");
                using var s = e.Open();
                s.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
            }
            // A non-image entry that must NOT be counted as a page.
            using (var meta = zip.CreateEntry("ComicInfo.xml").Open())
                meta.Write(Encoding.UTF8.GetBytes("<ComicInfo/>"));
        }
        return ms.ToArray();
    }

    private static byte[] Zip(string entryPath, string content)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var e = zip.CreateEntry(entryPath);
            using var s = e.Open();
            var b = Encoding.UTF8.GetBytes(content);
            s.Write(b, 0, b.Length);
        }
        return ms.ToArray();
    }
}

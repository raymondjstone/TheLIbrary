using SharpCompress.Archives;
using SharpCompress.Readers;

namespace TheLibrary.Server.Services.Calibre;

// Reads comic archives (.cbz = zip, .cbr = rar) natively via SharpCompress —
// no Calibre needed. Used by the integrity check to count "pages" (image
// entries) and could back a native CBR preview too.
public static class ComicArchive
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tif", ".tiff",
    };

    public static bool IsImage(string name) => ImageExtensions.Contains(Path.GetExtension(name));

    // Number of image entries in the archive, or -1 if it can't be opened.
    // SharpCompress auto-detects zip/rar/7z from the stream content.
    public static int CountImages(Stream stream)
    {
        try
        {
            using var archive = ArchiveFactory.OpenArchive(stream, new ReaderOptions());
            return archive.Entries.Count(e =>
                !e.IsDirectory && e.Key is not null && IsImage(e.Key));
        }
        catch
        {
            return -1;
        }
    }
}

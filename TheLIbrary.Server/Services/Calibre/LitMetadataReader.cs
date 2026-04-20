using System.Text;

namespace TheLibrary.Server.Services.Calibre;

// Microsoft Reader .lit files start with the ASCII magic "ITOLITLS" and store
// their OPF metadata inside an internal "/meta" stream that's LZX-compressed
// (and sometimes DRM-wrapped). A full decompressor is substantial work for a
// format Microsoft discontinued in 2012, so we validate the magic and let the
// caller fall back to filename-based routing for title/author.
public static class LitMetadataReader
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ITOLITLS");

    public static EpubMetadata? TryReadFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> header = stackalloc byte[8];
            if (fs.Read(header) != 8) return null;
            for (var i = 0; i < Magic.Length; i++)
                if (header[i] != Magic[i]) return null;

            // Real .lit file confirmed. No readable metadata here — the OPF
            // stream is LZX-compressed. Return null so the IncomingProcessor
            // falls back to parsing "Author - Title.lit" filenames.
            return null;
        }
        catch { return null; }
    }
}

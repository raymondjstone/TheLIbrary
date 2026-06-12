namespace TheLibrary.Server.Services.Calibre;

// Single extension→reader dispatch for the embedded metadata of an ebook file
// (EPUB OPF, FB2, MOBI EXTH, PDF info, CBZ ComicInfo, DOCX/ODT core props).
// Shared by the incoming processor and the content-scan / assign-authors
// pipeline so every consumer sees the same metadata for the same file.
public static class FileMetadataReader
{
    public static EpubMetadata? TryRead(string file, ILogger? log = null)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        var m = ext switch
        {
            ".epub" => EpubMetadataReader.TryReadFile(file),
            ".fb2" or ".fbz" => Fb2MetadataReader.TryReadFile(file),
            ".zip" when file.EndsWith(".fb2.zip", StringComparison.OrdinalIgnoreCase)
                => Fb2MetadataReader.TryReadFile(file),
            ".mobi" or ".azw" or ".azw3" or ".azw4" or ".kf8" or ".prc" or ".pdb"
                => MobiMetadataReader.TryReadFile(file),
            ".pdf" => PdfMetadataReader.TryReadFile(file, log),
            ".lit" => LitMetadataReader.TryReadFile(file),
            ".cbz" => CbzMetadataReader.TryReadFile(file),
            ".docx" or ".odt" => DocxMetadataReader.TryReadFile(file),
            ".opf" => OpfMetadataReader.TryReadFile(file),
            _ => null
        };
        return m is null ? null : Sanitize(m);
    }

    // A corrupt MOBI/PDB header can yield raw binary as the "title" — strings
    // with control characters are not metadata and must never reach consumers
    // (an OpenLibrary query with %00 bytes gets 403'd by their WAF). Any field
    // containing control characters is dropped.
    private static EpubMetadata Sanitize(EpubMetadata m) => m with
    {
        Title = Clean(m.Title),
        Author = Clean(m.Author),
        AuthorSort = Clean(m.AuthorSort),
        Series = Clean(m.Series),
        Subject = Clean(m.Subject),
        Isbn = Clean(m.Isbn),
    };

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        var t = s.Trim();
        return t.Any(char.IsControl) ? null : t;
    }
}

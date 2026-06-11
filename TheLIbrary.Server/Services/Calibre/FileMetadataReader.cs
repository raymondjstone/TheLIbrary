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
        return ext switch
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
    }
}

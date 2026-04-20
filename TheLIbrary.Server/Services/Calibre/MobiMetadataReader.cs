using System.Buffers.Binary;
using System.Text;

namespace TheLibrary.Server.Services.Calibre;

// Kindle MOBI / AZW / AZW3 metadata reader. Layout (big-endian throughout):
//   PalmDOC DB header    (78 bytes) + record info entries (8 bytes each)
//   record 0            -> PalmDOC header + MOBI header + optional EXTH header
//
// The MOBI header starts at record 0's data offset and has at offset 16 the
// identifier bytes "MOBI". Offset 84 is the full-name offset (relative to
// record 0), offset 88 the full-name length. Offset 128 holds a bit-flag
// whose 0x40 bit indicates an EXTH header follows the MOBI header. EXTH
// records hold author (type 100), publisher (101), subject (105), language
// (524 — rare), etc.
public static class MobiMetadataReader
{
    public static EpubMetadata? TryReadFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);

            // PDB: numRecords is at offset 76 (uint16 BE).
            fs.Seek(76, SeekOrigin.Begin);
            var numRecords = ReadUInt16BE(br);
            if (numRecords == 0) return null;

            // Record 0 offset lives at PDB offset 78 + 0 * 8 = 78.
            fs.Seek(78, SeekOrigin.Begin);
            var rec0Offset = ReadUInt32BE(br);
            // Record 1 offset (if present) tells us how long record 0 is.
            long rec0End;
            if (numRecords > 1)
            {
                fs.Seek(78 + 8, SeekOrigin.Begin);
                rec0End = ReadUInt32BE(br);
            }
            else rec0End = fs.Length;
            var rec0Len = rec0End - rec0Offset;
            if (rec0Len < 24 || rec0Offset + rec0Len > fs.Length) return null;

            // Skip PalmDOC header (16 bytes) to reach MOBI header.
            var mobiHeaderOffset = rec0Offset + 16;
            fs.Seek(mobiHeaderOffset, SeekOrigin.Begin);
            var ident = br.ReadBytes(4);
            if (Encoding.ASCII.GetString(ident) != "MOBI") return null;

            var mobiHeaderLen = ReadUInt32BE(br);
            fs.Seek(mobiHeaderOffset + 28, SeekOrigin.Begin);
            var textEncoding = ReadUInt32BE(br); // 1252 = win1252, 65001 = utf8
            var enc = textEncoding == 65001 ? Encoding.UTF8 :
                      textEncoding == 1252 ? Encoding.Latin1 : Encoding.UTF8;

            fs.Seek(mobiHeaderOffset + 84, SeekOrigin.Begin);
            var fullNameOffset = ReadUInt32BE(br);
            var fullNameLen = ReadUInt32BE(br);

            fs.Seek(mobiHeaderOffset + 128, SeekOrigin.Begin);
            var exthFlag = ReadUInt32BE(br);
            var hasExth = (exthFlag & 0x40) != 0;

            string? title = null;
            if (fullNameLen > 0 && fullNameLen < 1024
                && rec0Offset + fullNameOffset + fullNameLen <= fs.Length)
            {
                fs.Seek(rec0Offset + fullNameOffset, SeekOrigin.Begin);
                var raw = br.ReadBytes((int)fullNameLen);
                title = enc.GetString(raw).TrimEnd('\0').Trim();
            }

            string? author = null;
            string? publisher = null;
            string? language = null;
            if (hasExth)
            {
                var exthOffset = mobiHeaderOffset + mobiHeaderLen;
                fs.Seek(exthOffset, SeekOrigin.Begin);
                var exthId = br.ReadBytes(4);
                if (Encoding.ASCII.GetString(exthId) == "EXTH")
                {
                    _ = ReadUInt32BE(br); // header length
                    var recordCount = ReadUInt32BE(br);
                    for (uint i = 0; i < recordCount; i++)
                    {
                        if (fs.Position + 8 > fs.Length) break;
                        var recType = ReadUInt32BE(br);
                        var recLen = ReadUInt32BE(br);
                        if (recLen < 8) break;
                        var dataLen = (int)recLen - 8;
                        if (dataLen < 0 || fs.Position + dataLen > fs.Length) break;
                        var data = br.ReadBytes(dataLen);
                        var value = enc.GetString(data).TrimEnd('\0').Trim();
                        switch (recType)
                        {
                            case 100: author ??= value; break;
                            case 101: publisher ??= value; break;
                            case 524: language ??= value; break;
                        }
                    }
                }
            }

            if (title is null && author is null) return null;
            string? authorSort = null;
            if (!string.IsNullOrWhiteSpace(author))
            {
                // Kindle writes author as "First Last" or "Last, First"; prefer
                // the raw value as author_sort if it already looks flipped.
                authorSort = author.Contains(',') ? author : null;
            }

            return new EpubMetadata(title, author, authorSort, language);
        }
        catch { return null; }
    }

    private static ushort ReadUInt16BE(BinaryReader br)
    {
        Span<byte> buf = stackalloc byte[2];
        if (br.Read(buf) != 2) return 0;
        return BinaryPrimitives.ReadUInt16BigEndian(buf);
    }

    private static uint ReadUInt32BE(BinaryReader br)
    {
        Span<byte> buf = stackalloc byte[4];
        if (br.Read(buf) != 4) return 0;
        return BinaryPrimitives.ReadUInt32BigEndian(buf);
    }
}

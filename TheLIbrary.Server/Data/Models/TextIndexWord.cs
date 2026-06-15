namespace TheLibrary.Server.Data.Models;

// Inverted-index row: one (word, indexed-file) pair. Lets content search be an
// indexed prefix lookup on a compact in-row column, instead of a slow LIKE scan
// over the off-row nvarchar(max) text — and works on any SQL Server edition,
// with or without the Full-Text component. The (Word, TextIndexId) primary key
// is clustered, so `Word LIKE 'term%'` is an index seek.
public class TextIndexWord
{
    public string Word { get; set; } = "";

    public int TextIndexId { get; set; }
    public BookTextIndex TextIndex { get; set; } = null!;
}

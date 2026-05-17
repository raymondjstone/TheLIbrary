namespace TheLibrary.Server.Data.Models;
public class SeriesAuthor
{
    public int SeriesId { get; set; }
    public Series Series { get; set; } = null!;
    public int AuthorId { get; set; }
    public Author Author { get; set; } = null!;
}

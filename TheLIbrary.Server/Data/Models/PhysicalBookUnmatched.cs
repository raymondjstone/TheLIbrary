namespace TheLibrary.Server.Data.Models;

public class PhysicalBookUnmatched
{
    public int Id { get; set; }
    public string Author { get; set; } = "";
    public string Title { get; set; } = "";
    public string SeriesPos { get; set; } = "";
    public DateTime AddedAt { get; set; }
}

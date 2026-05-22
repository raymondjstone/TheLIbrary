namespace TheLibrary.Server.Data.Models;

public class PhysicalBookUnmatched
{
    public int Id { get; set; }
    public string Author { get; set; } = "";
    public string Title { get; set; } = "";
    public string SeriesPos { get; set; } = "";

    // ISBN-13/10 pulled off the import row when present — the highest-confidence
    // match key, used ahead of the title+author fuzzy match.
    public string? Isbn { get; set; }

    public DateTime AddedAt { get; set; }
}

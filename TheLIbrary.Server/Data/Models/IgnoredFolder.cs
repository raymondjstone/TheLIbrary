namespace TheLibrary.Server.Data.Models;

// A Calibre author-level folder name that the scanner should skip entirely,
// no matter which library location it sits under. Matched case-insensitively.
public class IgnoredFolder
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

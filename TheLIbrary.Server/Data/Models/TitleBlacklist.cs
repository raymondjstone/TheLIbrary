namespace TheLibrary.Server.Data.Models;

// A guessed "title" the content-scan front-matter parser must never keep.
// Junk strings like "Scanned", "Published", "@page" get pulled from copyright
// pages / OCR artefacts often enough that they're worth refusing outright
// rather than shipping them to OpenLibrary search as a real title guess.
public class TitleBlacklist
{
    public int Id { get; set; }

    // Display name preserved for the UI — the form the user typed or seeded.
    public string Title { get; set; } = string.Empty;

    // TitleNormalizer.Normalize(Title). Content-scan compares a guessed
    // title's normalized form against this for an exact match.
    public string NormalizedTitle { get; set; } = string.Empty;

    public DateTime AddedAt { get; set; }
}

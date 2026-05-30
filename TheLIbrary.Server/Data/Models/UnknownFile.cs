using System.ComponentModel.DataAnnotations;

namespace TheLibrary.Server.Data.Models;

// A raw ebook file sitting in the __unknown quarantine folder that isn't linked
// to any author or book. These are indexed into the DB during sync so the
// missing-works "find matching files" search can score them from the database
// instead of walking the filesystem on every request.
public class UnknownFile
{
    public int Id { get; set; }

    [MaxLength(2048)]
    public string FullPath { get; set; } = "";

    [MaxLength(512)]
    public string FileName { get; set; } = "";

    // Normalized filename stem — the fuzzy-match key scored against book titles.
    [MaxLength(1024)]
    public string? NormalizedTitle { get; set; }

    public long SizeBytes { get; set; }

    public DateTime ModifiedAt { get; set; }

    public DateTime ScannedAt { get; set; }
}

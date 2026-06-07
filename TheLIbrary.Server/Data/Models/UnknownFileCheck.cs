using System.ComponentModel.DataAnnotations;

namespace TheLibrary.Server.Data.Models;

// Records that an untracked file (one in the __unknown bucket, indexed in the
// UnknownFiles table) has been integrity-checked. Kept in its own table because
// the UnknownFiles index is wiped + rebuilt on every sync — this survives that,
// so healthy untracked files aren't re-checked on every run. A file is "still
// checked" only while its SizeBytes + ModifiedAt match what was recorded here.
public class UnknownFileCheck
{
    public int Id { get; set; }

    [MaxLength(2048)]
    public string FullPath { get; set; } = "";

    public long SizeBytes { get; set; }

    public DateTime ModifiedAt { get; set; }

    public DateTime CheckedAt { get; set; }
}

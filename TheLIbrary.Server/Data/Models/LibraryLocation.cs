using System.ComponentModel.DataAnnotations;

namespace TheLibrary.Server.Data.Models;

public class LibraryLocation
{
    public int Id { get; set; }

    [MaxLength(128)]
    public string? Label { get; set; }

    [MaxLength(1024)]
    public string Path { get; set; } = "";

    public bool Enabled { get; set; } = true;

    // Target root for files processed out of the incoming folder. Only one
    // location may be marked primary at any time; enforced in the controller.
    public bool IsPrimary { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? LastScanAt { get; set; }
}

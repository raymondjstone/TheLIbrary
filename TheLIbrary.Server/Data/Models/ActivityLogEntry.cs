using System.ComponentModel.DataAnnotations;

namespace TheLibrary.Server.Data.Models;

// Audit trail of consequential file actions (archive / delete / auto-archive),
// so "why did this move / where did it go?" is answerable without trawling logs.
// Read-only history — recorded by the action, shown on the Activity page.
public class ActivityLogEntry
{
    public int Id { get; set; }

    public DateTime At { get; set; }

    // Short action key, e.g. "archive", "delete", "auto-archive", "convert".
    [MaxLength(40)]
    public string Action { get; set; } = "";

    // Who triggered it: "user" (a UI action) or a job id (e.g. "duplicate-auto-archive").
    [MaxLength(40)]
    public string Source { get; set; } = "user";

    // Human-readable description (what moved, from/to, counts).
    [MaxLength(2048)]
    public string Detail { get; set; } = "";

    // Optional link back to the affected book.
    public int? BookId { get; set; }
}

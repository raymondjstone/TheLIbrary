using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;

namespace TheLibrary.Server.Services;

// Records a consequential file action to the activity log. Adds the row to the
// context; the caller's SaveChanges persists it (so it commits atomically with
// the action it describes).
public static class ActivityLogger
{
    public static void Record(LibraryDbContext db, string action, string detail, string source = "user", int? bookId = null)
        => db.ActivityLog.Add(new ActivityLogEntry
        {
            At = DateTime.UtcNow,
            Action = action,
            Source = source,
            Detail = detail.Length <= 2048 ? detail : detail[..2048],
            BookId = bookId,
        });
}

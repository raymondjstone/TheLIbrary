using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace TheLibrary.Server.Services.Sync;

// EF wraps the real cause in InnerException (typically a SqlException) and
// ToString() dumps the whole stack — neither is useful in the UI. This walks
// the chain and keeps the bits that actually tell you what went wrong.
public static class ExceptionFormatter
{
    public static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        var current = (Exception?)ex;
        int depth = 0;
        while (current is not null && depth < 6)
        {
            parts.Add(Describe(current));
            current = current.InnerException;
            depth++;
        }
        return string.Join(" → ", parts);
    }

    private static string Describe(Exception ex)
    {
        var type = ex.GetType().Name;
        return ex switch
        {
            SqlException sql  => $"[{type} #{sql.Number}] {sql.Message.Trim()}",
            DbUpdateException => $"[{type}] {ex.Message.Trim()}",
            _                 => $"[{type}] {ex.Message.Trim()}",
        };
    }
}

namespace TheLibrary.Server.Services.Sync;

public sealed class AuthorRefreshAlreadyRunningException : Exception
{
    public AuthorRefreshAlreadyRunningException(int authorId, string authorName)
        : base($"Author '{authorName}' is already being refreshed.")
    {
        AuthorId = authorId;
    }

    public int AuthorId { get; }
}

// Per-author gate for works refreshes. This allows manual author refreshes to
// overlap with Hangfire jobs while still preventing duplicate refreshes of the
// same author.
public sealed class AuthorRefreshCoordinator
{
    private readonly object _lock = new();
    private readonly HashSet<int> _running = new();

    public bool TryAcquire(int authorId)
    {
        lock (_lock) return _running.Add(authorId);
    }

    public void Release(int authorId)
    {
        lock (_lock) _running.Remove(authorId);
    }
}

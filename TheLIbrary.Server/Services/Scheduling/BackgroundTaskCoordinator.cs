namespace TheLibrary.Server.Services.Scheduling;

// One global gate that mutually excludes every long-running background task
// (sync / seed / author-updates / incoming). Both the UI-triggered paths and
// the Hangfire-scheduled paths go through here, so a scheduled job can't
// kick off while a manual run is in flight and vice versa.
//
// Replacement for the per-service SemaphoreSlim _runGate each service held
// before — we consolidate them so cross-service overlap (e.g. Sync + Incoming)
// also gets blocked, not just within a single service.
public sealed class BackgroundTaskCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _holderLock = new();
    private string? _holder;

    public bool TryAcquire(string taskName, out string? currentHolder)
    {
        if (!_gate.Wait(0))
        {
            lock (_holderLock) currentHolder = _holder;
            return false;
        }
        lock (_holderLock) _holder = taskName;
        currentHolder = null;
        return true;
    }

    public void Release()
    {
        lock (_holderLock) _holder = null;
        _gate.Release();
    }

    public string? CurrentHolder
    {
        get { lock (_holderLock) return _holder; }
    }

    public bool IsBusy
    {
        get { lock (_holderLock) return _holder is not null; }
    }
}

using TheLibrary.Server.Services.Scheduling;

namespace TheLibrary.Server.Services.Incoming;

public enum IncomingPhase { Idle, Running, Done, Failed }

public sealed class IncomingState
{
    public IncomingPhase Phase { get; set; } = IncomingPhase.Idle;
    public string? Message { get; set; }
    public int Processed { get; set; }
    public int Matched { get; set; }
    public int UnknownAuthor { get; set; }
    public int Skipped { get; set; }
    public int Errors { get; set; }
    public List<string> Log { get; set; } = new();
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? Error { get; set; }
}

// Snapshot the processor pushes every time it finishes a folder or advances
// a counter. The singleton service folds this into its shared state so the
// UI can see progress without waiting for the run to finish.
public sealed record IncomingProgress(
    int Processed,
    int Matched,
    int Unknown,
    int Skipped,
    int Errors,
    string? Message,
    string? LogLine);

// Singleton wrapper: kicks off IncomingProcessor on a background task and
// exposes a pollable state object. Mirrors SyncService's lifecycle pattern
// (TryStart + GetState + a SemaphoreSlim run gate).
public sealed class IncomingService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<IncomingService> _log;
    private readonly BackgroundTaskCoordinator _coordinator;
    private readonly object _stateLock = new();
    private IncomingState _state = new();

    public IncomingService(IServiceScopeFactory scopeFactory, BackgroundTaskCoordinator coordinator, ILogger<IncomingService> log)
    {
        _scopeFactory = scopeFactory;
        _coordinator = coordinator;
        _log = log;
    }

    public IncomingState GetState()
    {
        lock (_stateLock) return Clone(_state);
    }

    public bool IsRunning
    {
        get { lock (_stateLock) return _state.Phase == IncomingPhase.Running; }
    }

    public bool TryStart(CancellationToken hostCt, out string? error)
        => TryStartInternal("incoming", (p, ct) => p.ProcessAsync(Report, ct), hostCt, out error);

    // Scans <primary>/Unknown and tries to re-resolve authors against the
    // current author index, moving matched files into their proper author
    // folder. Unmatched files stay put. Uses the same coordinator slot as the
    // normal incoming run so the two can't overlap.
    public bool TryStartUnknown(CancellationToken hostCt, out string? error)
        => TryStartInternal("reprocess-unknown", (p, ct) => p.ProcessUnknownAsync(Report, ct), hostCt, out error);

    private bool TryStartInternal(
        string coordinatorKey,
        Func<IncomingProcessor, CancellationToken, Task<IncomingResult>> run,
        CancellationToken hostCt,
        out string? error)
    {
        if (!_coordinator.TryAcquire(coordinatorKey, out var holder))
        {
            error = $"Another task is already running ({holder})";
            return false;
        }
        error = null;

        lock (_stateLock) _state = new IncomingState
        {
            Phase = IncomingPhase.Running,
            StartedAt = DateTime.UtcNow,
            Message = "Starting"
        };

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<IncomingProcessor>();
                var result = await run(processor, hostCt);
                MutateState(s =>
                {
                    s.Phase = IncomingPhase.Done;
                    s.FinishedAt = DateTime.UtcNow;
                    s.Processed = result.Processed;
                    s.Matched = result.Matched;
                    s.UnknownAuthor = result.UnknownAuthor;
                    s.Skipped = result.Skipped;
                    s.Errors = result.Errors;
                    s.Log = result.Log.ToList();
                    s.Message = "Complete";
                });
            }
            catch (InvalidOperationException ex)
            {
                MutateState(s => { s.Phase = IncomingPhase.Failed; s.Error = ex.Message; s.FinishedAt = DateTime.UtcNow; });
            }
            catch (OperationCanceledException)
            {
                MutateState(s => { s.Phase = IncomingPhase.Failed; s.Error = "Canceled"; s.FinishedAt = DateTime.UtcNow; });
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "{Job} processing failed", coordinatorKey);
                MutateState(s => { s.Phase = IncomingPhase.Failed; s.Error = ex.Message; s.FinishedAt = DateTime.UtcNow; });
            }
            finally { _coordinator.Release(); }
        }, hostCt);

        return true;
    }

    private void Report(IncomingProgress p)
    {
        MutateState(s =>
        {
            s.Processed = p.Processed;
            s.Matched = p.Matched;
            s.UnknownAuthor = p.Unknown;
            s.Skipped = p.Skipped;
            s.Errors = p.Errors;
            if (!string.IsNullOrEmpty(p.Message)) s.Message = p.Message;
            if (p.LogLine is not null)
            {
                s.Log.Add(p.LogLine);
                // Cap the in-memory log so long runs don't balloon the state.
                const int max = 500;
                if (s.Log.Count > max) s.Log.RemoveRange(0, s.Log.Count - max);
            }
        });
    }

    private void MutateState(Action<IncomingState> mutate)
    {
        lock (_stateLock) mutate(_state);
    }

    private static IncomingState Clone(IncomingState s) => new()
    {
        Phase = s.Phase,
        Message = s.Message,
        Processed = s.Processed,
        Matched = s.Matched,
        UnknownAuthor = s.UnknownAuthor,
        Skipped = s.Skipped,
        Errors = s.Errors,
        Log = s.Log.ToList(),
        StartedAt = s.StartedAt,
        FinishedAt = s.FinishedAt,
        Error = s.Error
    };
}

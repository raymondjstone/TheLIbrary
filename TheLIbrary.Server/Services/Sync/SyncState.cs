namespace TheLibrary.Server.Services.Sync;

public enum SyncPhase
{
    Idle,
    SeedingAuthors,
    ScanningCalibre,
    ResolvingAuthors,
    FetchingWorks,
    Matching,
    AuthorUpdates,
    Done,
    Failed
}

public sealed class SyncState
{
    public SyncPhase Phase { get; set; } = SyncPhase.Idle;
    public string? Message { get; set; }
    public int AuthorsTotal { get; set; }
    public int AuthorsProcessed { get; set; }
    public int BooksAdded { get; set; }
    public int LocalFilesSeen { get; set; }

    // Populated during the SeedingAuthors phase.
    public long DumpBytesDone { get; set; }
    public long? DumpBytesTotal { get; set; }
    public long DumpRowsParsed { get; set; }
    public long DumpAuthorsInserted { get; set; }

    // Populated during the AuthorUpdates phase.
    public int UpdateDaysTotal { get; set; }
    public int UpdateDaysProcessed { get; set; }
    public int UpdateMergesSeen { get; set; }
    public int UpdateAuthorsRekeyed { get; set; }
    public int UpdateAuthorsFolded { get; set; }
    public string? UpdateCurrentDay { get; set; }

    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? Error { get; set; }
}

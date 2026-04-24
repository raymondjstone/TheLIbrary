using Microsoft.AspNetCore.Mvc;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SyncController : ControllerBase
{
    private readonly SyncService _sync;
    private readonly IHostApplicationLifetime _lifetime;

    public SyncController(SyncService sync, IHostApplicationLifetime lifetime)
    {
        _sync = sync;
        _lifetime = lifetime;
    }

    [HttpGet("status")]
    public ActionResult<SyncState> Status() => _sync.GetState();

    [HttpPost("start")]
    public ActionResult Start()
    {
        if (!_sync.TryStart(_lifetime.ApplicationStopping, out var err))
            return Conflict(new { error = err });
        return Accepted(_sync.GetState());
    }

    [HttpPost("seed-authors")]
    public ActionResult SeedAuthors()
    {
        if (!_sync.TryStartSeed(_lifetime.ApplicationStopping, out var err))
            return Conflict(new { error = err });
        return Accepted(_sync.GetState());
    }

    [HttpPost("author-updates")]
    public ActionResult AuthorUpdates()
    {
        if (!_sync.TryStartAuthorUpdates(_lifetime.ApplicationStopping, out var err))
            return Conflict(new { error = err });
        return Accepted(_sync.GetState());
    }

    [HttpPost("refresh-works")]
    public ActionResult RefreshWorks()
    {
        if (!_sync.TryStartRefreshDueWorks(_lifetime.ApplicationStopping, out var err))
            return Conflict(new { error = err });
        return Accepted(_sync.GetState());
    }
}

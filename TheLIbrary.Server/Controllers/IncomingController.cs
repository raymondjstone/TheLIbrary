using Microsoft.AspNetCore.Mvc;
using TheLibrary.Server.Services.Incoming;

namespace TheLibrary.Server.Controllers;

[ApiController]
[Route("api/incoming")]
public class IncomingController : ControllerBase
{
    private readonly IncomingService _svc;
    public IncomingController(IncomingService svc) { _svc = svc; }

    // Kicks off incoming processing on a background task and returns
    // immediately. Poll /state to see progress.
    [HttpPost("process")]
    public IActionResult Process(CancellationToken ct)
    {
        if (!_svc.TryStart(ct, out var error))
            return Conflict(new { error });
        return Accepted(_svc.GetState());
    }

    // Re-runs the author-matching pass against files already sitting in the
    // primary library's Unknown bucket. Matched files move to their author
    // folder; unmatched files stay put.
    [HttpPost("reprocess-unknown")]
    public IActionResult ReprocessUnknown(CancellationToken ct)
    {
        if (!_svc.TryStartUnknown(ct, out var error))
            return Conflict(new { error });
        return Accepted(_svc.GetState());
    }

    [HttpGet("state")]
    public IActionResult State() => Ok(_svc.GetState());
}

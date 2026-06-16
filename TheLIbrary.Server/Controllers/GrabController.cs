using Microsoft.AspNetCore.Mvc;
using TheLibrary.Server.Services.Download;

namespace TheLibrary.Server.Controllers;

[ApiController]
[Route("api/books")]
public class GrabController : ControllerBase
{
    private readonly NzbGrabService _grab;
    public GrabController(NzbGrabService grab) { _grab = grab; }

    // POST /api/books/{id}/grab — search the configured indexer for this book
    // and send the best NZB to SABnzbd. Errors surface as JSON via the filter.
    [HttpPost("{id:int}/grab")]
    public async Task<IActionResult> Grab(int id, CancellationToken ct)
    {
        var result = await _grab.GrabAsync(id, ct);
        return result.Success ? Ok(result) : BadRequest(result);
    }
}

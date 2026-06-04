using Microsoft.AspNetCore.Mvc;
using TheLibrary.Server.Services.OpenLibrary;

namespace TheLibrary.Server.Controllers;

// Serves cover images cached by OpenLibraryMetadataCacheService. The directory
// is a user setting (and may live outside wwwroot, e.g. on the library mount),
// so we stream the file ourselves rather than relying on static-file middleware
// pinned to a fixed path at startup.
[ApiController]
public class CachedCoversController : ControllerBase
{
    private readonly CoverCacheState _state;

    public CachedCoversController(CoverCacheState state) => _state = state;

    [HttpGet("/cached-covers/{name}")]
    public IActionResult Get(string name)
    {
        var dir = _state.Directory;
        if (string.IsNullOrEmpty(dir)) return NotFound();
        // Only a bare filename is allowed — no path separators or traversal.
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\') || name.Contains(".."))
            return BadRequest();

        var path = Path.Combine(dir, name);
        if (!System.IO.File.Exists(path)) return NotFound();

        // Cached covers are content-addressed by OL cover id, so they never
        // change — let the browser cache them aggressively.
        Response.Headers.CacheControl = "public, max-age=2592000, immutable";
        return PhysicalFile(path, "image/jpeg");
    }
}

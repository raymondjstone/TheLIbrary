using Microsoft.AspNetCore.Mvc;
using TheLibrary.Server.Services.Search;

namespace TheLibrary.Server.Controllers;

// Full-text search over indexed ebook text (opt-in; see FullTextSearchService).
[ApiController]
[Route("api/[controller]")]
public class SearchController : ControllerBase
{
    private readonly FullTextSearchService _fts;
    private readonly IHostApplicationLifetime _lifetime;
    public SearchController(FullTextSearchService fts, IHostApplicationLifetime lifetime)
    {
        _fts = fts; _lifetime = lifetime;
    }

    // GET /api/search?q=...
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] string? q, CancellationToken ct)
    {
        try { return Ok(await _fts.SearchAsync(q, limit: 60, ct)); }
        catch (OperationCanceledException) { return Ok(new FullTextSearchService.SearchResponse(true, q ?? "", Array.Empty<FullTextSearchService.SearchHit>())); }
        // Always return JSON (never an HTML error page) so the client can show a clean message.
        catch (Exception ex) { return StatusCode(500, new { error = $"Search failed: {ex.Message}" }); }
    }

    // GET /api/search/status — enabled flag + index progress for the UI.
    [HttpGet("status")]
    public Task<FullTextSearchService.IndexStatus> Status(CancellationToken ct)
        => _fts.StatusAsync(ct);

    // POST /api/search/run — kick off a background indexing run (one batch of up
    // to FullTextIndexMaxPerRun books). Returns immediately; poll /status.
    [HttpPost("run")]
    public IActionResult Run()
    {
        if (!_fts.TryStart(_lifetime.ApplicationStopping, out var error))
            return Conflict(new { error });
        return Ok(new { started = true });
    }

    // POST /api/search/clear — drop the whole index (rebuild / reclaim space).
    [HttpPost("clear")]
    public async Task<IActionResult> Clear(CancellationToken ct)
    {
        var removed = await _fts.ClearAsync(ct);
        return Ok(new { removed });
    }
}

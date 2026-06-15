using Microsoft.AspNetCore.Mvc;
using TheLibrary.Server.Services.Search;

namespace TheLibrary.Server.Controllers;

// Full-text search over indexed ebook text (opt-in; see FullTextSearchService).
[ApiController]
[Route("api/[controller]")]
public class SearchController : ControllerBase
{
    private readonly FullTextSearchService _fts;
    public SearchController(FullTextSearchService fts) { _fts = fts; }

    // GET /api/search?q=...
    [HttpGet]
    public Task<FullTextSearchService.SearchResponse> Search([FromQuery] string? q, CancellationToken ct)
        => _fts.SearchAsync(q, limit: 60, ct);

    // GET /api/search/status — enabled flag + index progress for the UI.
    [HttpGet("status")]
    public Task<FullTextSearchService.IndexStatus> Status(CancellationToken ct)
        => _fts.StatusAsync(ct);

    // POST /api/search/reindex — index one batch of not-yet-indexed books. The
    // UI calls this repeatedly until Remaining hits 0. No-op when disabled.
    [HttpPost("reindex")]
    public Task<FullTextSearchService.IndexResult> Reindex(CancellationToken ct)
        => _fts.IndexBatchAsync(FullTextSearchService.DefaultBatch, ct);

    // POST /api/search/clear — drop the whole index (rebuild / reclaim space).
    [HttpPost("clear")]
    public async Task<IActionResult> Clear(CancellationToken ct)
    {
        var removed = await _fts.ClearAsync(ct);
        return Ok(new { removed });
    }
}

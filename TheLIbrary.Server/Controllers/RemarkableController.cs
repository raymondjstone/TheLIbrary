using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Services.Remarkable;

namespace TheLibrary.Server.Controllers;

[ApiController]
[Route("api/remarkable")]
public class RemarkableController : ControllerBase
{
    private readonly RemarkableClient _rm;
    private readonly LibraryDbContext _db;

    public RemarkableController(RemarkableClient rm, LibraryDbContext db)
    {
        _rm = rm;
        _db = db;
    }

    public sealed record StatusDto(
        bool Connected,
        DateTime? ConnectedAt,
        DateTime? LastSentAt);

    [HttpGet("status")]
    public async Task<StatusDto> Status(CancellationToken ct)
    {
        var a = await _rm.GetAuthAsync(ct);
        return a is null
            ? new StatusDto(false, null, null)
            : new StatusDto(true, a.ConnectedAt, a.LastSentAt);
    }

    public sealed record ConnectRequest(string Code);

    [HttpPost("connect")]
    public async Task<ActionResult<StatusDto>> Connect([FromBody] ConnectRequest body, CancellationToken ct)
    {
        try
        {
            var a = await _rm.ConnectAsync(body.Code, ct);
            return new StatusDto(true, a.ConnectedAt, a.LastSentAt);
        }
        catch (RemarkableException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect(CancellationToken ct)
    {
        await _rm.DisconnectAsync(ct);
        return NoContent();
    }

    public sealed record SendResult(string Title);

    [HttpPost("send/{localFileId:int}")]
    public async Task<ActionResult<SendResult>> Send(int localFileId, CancellationToken ct)
    {
        var file = await _db.LocalBookFiles
            .Include(f => f.Book)
            .FirstOrDefaultAsync(f => f.Id == localFileId, ct);
        if (file is null) return NotFound(new { error = "Local file not found" });

        try
        {
            var title = await _rm.SendFileAsync(file, ct);
            return new SendResult(title);
        }
        catch (RemarkableException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Data;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Controllers;

// Read + light-action surface for the Damaged page: lists ebook files the
// integrity job flagged as unopenable / unconvertible / too short, lets the
// user re-queue one for a fresh check, and kicks off the job on demand.
[ApiController]
[Route("api/damaged")]
public class DamagedController : ControllerBase
{
    private readonly LibraryDbContext _db;
    private readonly BookIntegrityService _integrity;
    private readonly IHostApplicationLifetime _lifetime;

    public DamagedController(LibraryDbContext db, BookIntegrityService integrity, IHostApplicationLifetime lifetime)
    {
        _db = db;
        _integrity = integrity;
        _lifetime = lifetime;
    }

    public sealed record DamagedFile(
        int Id,
        int? BookId,
        string Title,
        int? AuthorId,
        string AuthorName,
        string Path,
        string? Format,
        long SizeBytes,
        int? Pages,
        string? Error,
        DateTime? CheckedAt);

    public sealed record JobStatus(bool Running, string? Message, int DamagedCount);

    /// <summary>Damaged files, newest-checked first. GET /api/damaged</summary>
    [HttpGet]
    public async Task<IReadOnlyList<DamagedFile>> GetDamaged(CancellationToken ct = default)
    {
        var rows = await _db.LocalBookFiles.AsNoTracking()
            .Where(f => f.IntegrityOk == false)
            .OrderByDescending(f => f.IntegrityCheckedAt)
            .Select(f => new DamagedFile(
                f.Id,
                f.BookId,
                f.Book != null ? f.Book.Title : f.TitleFolder,
                f.Book != null ? (int?)f.Book.AuthorId : f.AuthorId,
                f.Book != null ? f.Book.Author.Name : f.AuthorFolder,
                f.FullPath,
                null,
                f.SizeBytes,
                f.IntegrityPages,
                f.IntegrityError,
                f.IntegrityCheckedAt))
            .ToListAsync(ct);

        // Format is derived in memory — Path.GetExtension isn't translatable.
        return rows
            .Select(r => r with { Format = FormatOf(r.Path) })
            .ToList();
    }

    /// <summary>Current job status + damaged count for the page header. GET /api/damaged/status</summary>
    [HttpGet("status")]
    public async Task<JobStatus> GetStatus(CancellationToken ct = default)
    {
        var count = await _db.LocalBookFiles.AsNoTracking()
            .CountAsync(f => f.IntegrityOk == false, ct);
        return new JobStatus(_integrity.IsRunning, _integrity.CurrentMessage, count);
    }

    /// <summary>
    /// Re-queues a single file: clears its stored check fingerprint so the next
    /// job run re-checks it. POST /api/damaged/{id}/recheck
    /// </summary>
    [HttpPost("{id:int}/recheck")]
    public async Task<IActionResult> Recheck(int id, CancellationToken ct)
    {
        var file = await _db.LocalBookFiles.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (file is null) return NotFound(new { error = "File not found." });

        file.IntegrityCheckedSize = null;
        file.IntegrityOk = null;
        file.IntegrityError = null;
        file.IntegrityPages = null;
        file.IntegrityCheckedAt = null;
        await _db.SaveChangesAsync(ct);
        return Ok(new { requeued = true });
    }

    /// <summary>Starts the integrity job now. POST /api/damaged/run</summary>
    [HttpPost("run")]
    public IActionResult Run()
    {
        if (!_integrity.TryStart(_lifetime.ApplicationStopping, out var error))
            return Conflict(new { error });
        return Ok(new { started = true });
    }

    private static string? FormatOf(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return null;
        var ext = Path.GetExtension(fullPath).TrimStart('.').ToLowerInvariant();
        return string.IsNullOrEmpty(ext) ? null : ext;
    }
}

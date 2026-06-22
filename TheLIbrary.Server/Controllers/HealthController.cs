using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;

namespace TheLibrary.Server.Controllers;

// Operational/health view: the backlogs and the author-creation picture that
// the curated Home dashboard doesn't show. Cached briefly — it's a diagnostics
// page, not real-time. Pairs with /api/jobs/status (job state) on the client.
[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private readonly LibraryDbContext _db;
    private readonly IMemoryCache _cache;
    public HealthController(LibraryDbContext db, IMemoryCache cache) { _db = db; _cache = cache; }

    public sealed record Bucket(string Key, int Count);
    public sealed record HealthReport(
        int TotalAuthors,
        IReadOnlyList<Bucket> AuthorsByStatus,
        IReadOnlyList<Bucket> AuthorsBySource,
        IReadOnlyList<Bucket> AuthorsCreatedByDay,
        int EmptyPrunableAuthors,
        int UnmatchedFiles,
        int UntrackedScans,
        int UnknownFiles,
        int UntrackedNotLlmParsed,
        bool LlmEnabled,
        int LlmUsedToday,
        int LlmMaxPerDay,
        int LlmCallsLeftToday);

    [HttpGet]
    public Task<HealthReport> Get(CancellationToken ct)
        => _cache.GetOrCreateAsync("health:report", e =>
        {
            e.AbsoluteExpirationRelativeToNow = CacheTtl;
            return ComputeAsync(ct);
        })!;

    private static readonly string[] PrunableSources = { "same-name", "content-scan", "assign", "adopt" };

    private async Task<HealthReport> ComputeAsync(CancellationToken ct)
    {
        var statusRaw = await _db.Authors.AsNoTracking()
            .GroupBy(a => a.Status)
            .Select(g => new { g.Key, N = g.Count() })
            .ToListAsync(ct);
        var byStatus = statusRaw
            .Select(r => new Bucket(r.Key.ToString(), r.N))
            .OrderByDescending(b => b.Count).ToList();

        var sourceRaw = await _db.Authors.AsNoTracking()
            .GroupBy(a => a.CreationSource)
            .Select(g => new { g.Key, N = g.Count() })
            .ToListAsync(ct);
        var bySource = sourceRaw
            .Select(r => new Bucket(r.Key ?? "(pre-existing)", r.N))
            .OrderByDescending(b => b.Count).ToList();

        // Author creation over the last 14 days (uses the new CreatedAt audit
        // column) — shows the daily inflow at a glance.
        var since = DateTime.UtcNow.AddDays(-14);
        var dayRaw = await _db.Authors.AsNoTracking()
            .Where(a => a.CreatedAt >= since)
            .GroupBy(a => a.CreatedAt.Date)
            .Select(g => new { g.Key, N = g.Count() })
            .ToListAsync(ct);
        var byDay = dayRaw
            .OrderByDescending(r => r.Key)
            .Select(r => new Bucket(r.Key.ToString("yyyy-MM-dd"), r.N))
            .ToList();

        // What the prune-authors job would currently remove.
        var emptyPrunable = await _db.Authors.AsNoTracking().CountAsync(a =>
            a.CreationSource != null && PrunableSources.Contains(a.CreationSource)
            && (a.Status == AuthorStatus.Pending || a.Status == AuthorStatus.NotFound)
            && a.Priority == 0 && a.LinkedToAuthorId == null && !a.NotifyOnNewBooks
            && (a.Notes == null || a.Notes == "")
            && !_db.Books.Any(b => b.AuthorId == a.Id)
            && !_db.LocalBookFiles.Any(f => f.AuthorId == a.Id)
            && !_db.Authors.Any(x => x.LinkedToAuthorId == a.Id), ct);

        var totalAuthors = byStatus.Sum(b => b.Count);
        var unmatchedFiles = await _db.LocalBookFiles.AsNoTracking().CountAsync(f => f.AuthorId == null, ct);
        var untrackedScans = await _db.BookContentScans.AsNoTracking().CountAsync(s => s.Source == "untracked", ct);
        var unknownFiles = await _db.UnknownFiles.CountAsync(ct);
        // What the llm-identify job still has to do: untracked, author-less scans
        // not yet sent to the LLM (its exact candidate predicate).
        var untrackedNotLlmParsed = await _db.BookContentScans.AsNoTracking().CountAsync(
            s => s.Source == "untracked" && s.AuthorId == null && s.Author == null && s.LlmAttemptedAt == null, ct);

        // LLM "credits": the remaining daily-cap budget we enforce (the real
        // provider account balance isn't exposed via the API key). Resets at UTC
        // midnight; only meaningful when the LLM is enabled.
        var llmCfg = await TheLibrary.Server.Services.Llm.LlmMetadataClient.LoadConfigAsync(_db, ct);
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var llmUsage = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.LlmUsageDate || s.Key == AppSettingKeys.LlmUsageCount)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var llmUsedToday = llmUsage.GetValueOrDefault(AppSettingKeys.LlmUsageDate) == today
            && int.TryParse(llmUsage.GetValueOrDefault(AppSettingKeys.LlmUsageCount), out var lc) ? lc : 0;
        var llmCallsLeft = llmCfg.Enabled ? Math.Max(0, llmCfg.MaxPerDay - llmUsedToday) : 0;

        return new HealthReport(totalAuthors, byStatus, bySource, byDay, emptyPrunable,
            unmatchedFiles, untrackedScans, unknownFiles, untrackedNotLlmParsed,
            llmCfg.Enabled, llmUsedToday, llmCfg.MaxPerDay, llmCallsLeft);
    }
}

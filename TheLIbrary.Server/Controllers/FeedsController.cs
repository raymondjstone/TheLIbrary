using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Xml.Linq;
using TheLibrary.Server.Data;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Controllers;

// RSS 2.0 feeds — complement the OPDS Atom catalogue with a format every feed
// reader (and IFTTT/automation) understands. Subscribe to keep up with new
// releases from your watched authors without opening the app.
[ApiController]
[Route("rss")]
public class FeedsController : ControllerBase
{
    private readonly LibraryDbContext _db;
    public FeedsController(LibraryDbContext db) { _db = db; }

    private string BaseUrl => $"{Request.Scheme}://{Request.Host}";

    // New releases from starred authors (last 5 years), newest first. `all=true`
    // widens it to every tracked author, mirroring the Recent Releases page.
    [HttpGet("recent.xml")]
    public async Task<ContentResult> Recent([FromQuery] bool all, CancellationToken ct)
    {
        var cutoffYear = DateTime.UtcNow.Year - 5;
        var rows = await _db.Books.AsNoTracking()
            .Where(b => (all || b.Author.Priority >= 1)
                     && !b.Suppressed
                     && b.FirstPublishYear != null
                     && b.FirstPublishYear >= cutoffYear)
            .Select(b => new
            {
                b.Id, b.Title, b.NormalizedTitle, b.FirstPublishYear,
                b.OpenLibraryWorkKey, b.AuthorId,
                AuthorName = b.Author.Name,
                Owned = b.ManuallyOwned || b.LocalFiles.Any(),
            })
            .ToListAsync(ct);

        // Same per-author/title dedupe (keep earliest) as the Recent Releases page.
        var items = rows
            .GroupBy(r => r.NormalizedTitle is null ? $"\0{r.Id}" : $"{r.AuthorId}\0{r.NormalizedTitle}")
            .Select(g => g.MinBy(r => r.FirstPublishYear)!)
            .OrderByDescending(r => r.FirstPublishYear)
            .ThenBy(r => r.AuthorName)
            .ThenBy(r => r.Title)
            .Take(200)
            .Select(r =>
            {
                // Year is the finest publish granularity OpenLibrary gives, so
                // pubDate is Jan 1 of that year — enough for readers to sort by.
                var pubDate = new DateTime(r.FirstPublishYear!.Value, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var link = ManualWorkKey.IsManual(r.OpenLibraryWorkKey)
                    ? $"{BaseUrl}/authors/{r.AuthorId}"
                    : $"https://openlibrary.org/works/{r.OpenLibraryWorkKey}";
                return new XElement("item",
                    new XElement("title", $"{r.AuthorName} — {r.Title} ({r.FirstPublishYear})"),
                    new XElement("link", link),
                    new XElement("guid", new XAttribute("isPermaLink", "false"), $"thelibrary:book:{r.Id}"),
                    new XElement("author", r.AuthorName),
                    new XElement("pubDate", pubDate.ToString("r")),
                    new XElement("description", r.Owned ? "Owned" : "Missing"));
            });

        var scope = all ? "all tracked authors" : "starred authors";
        var channel = new XElement("channel",
            new XElement("title", $"The Library — New Releases ({scope})"),
            new XElement("link", $"{BaseUrl}/recent-releases"),
            new XElement("description", $"Works published since {cutoffYear} by {scope}."),
            new XElement("lastBuildDate", DateTime.UtcNow.ToString("r")),
            items);

        var rss = new XElement("rss", new XAttribute("version", "2.0"), channel);
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), rss);
        return Content(doc.ToString(), "application/rss+xml;charset=utf-8");
    }
}

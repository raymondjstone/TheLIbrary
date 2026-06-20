using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Xml.Linq;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Sync;

namespace TheLibrary.Server.Controllers;

// OPDS 1.2 Atom feed — lets reading apps (KOReader, Moon+ Reader, etc.)
// browse the library. Navigation-only: no file downloads are exposed since the
// files live on a local filesystem path, not a web-accessible URL.
[ApiController]
[Route("opds")]
public class OPDSController : ControllerBase
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace OpdsNs = "http://opds-spec.org/2010/catalog";
    private static readonly XNamespace Dc = "http://purl.org/dc/terms/";

    private readonly LibraryDbContext _db;
    public OPDSController(LibraryDbContext db) { _db = db; }

    private string BaseUrl => $"{Request.Scheme}://{Request.Host}";

    [HttpGet("catalog.xml")]
    public async Task<ContentResult> Catalog(CancellationToken ct)
    {
        var authorCount = await _db.Authors.CountAsync(a => a.Status == AuthorStatus.Active, ct);
        var feed = BuildFeed(
            id: "thelibrary:root",
            title: "The Library",
            entries: new[]
            {
                NavEntry("thelibrary:authors", "Authors",
                    $"All {authorCount} tracked authors",
                    $"{BaseUrl}/opds/authors.xml"),
                NavEntry("thelibrary:missing", "Missing Works",
                    "Books you don't own yet from starred authors",
                    $"{BaseUrl}/opds/missing.xml"),
                NavEntry("thelibrary:recent", "Recent Releases",
                    "Books published in the last 5 years",
                    $"{BaseUrl}/opds/recent.xml"),
            });

        return Xml(feed);
    }

    [HttpGet("authors.xml")]
    public async Task<ContentResult> Authors(CancellationToken ct)
    {
        var authors = await _db.Authors.AsNoTracking()
            .Where(a => a.Status == AuthorStatus.Active)
            .OrderBy(a => a.Name)
            .Select(a => new { a.Id, a.Name, a.Priority, BookCount = a.Books.Count() })
            .ToListAsync(ct);

        var entries = authors.Select(a => NavEntry(
            $"thelibrary:author:{a.Id}",
            a.Name,
            $"{a.BookCount} works",
            $"{BaseUrl}/opds/authors/{a.Id}.xml"));

        return Xml(BuildFeed("thelibrary:authors", "Authors", entries));
    }

    [HttpGet("authors/{id:int}.xml")]
    public async Task<ActionResult<ContentResult>> AuthorFeed(int id, CancellationToken ct)
    {
        var author = await _db.Authors.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (author is null) return NotFound();

        var books = await _db.Books.AsNoTracking()
            .Where(b => b.AuthorId == id)
            .OrderBy(b => b.FirstPublishYear ?? int.MaxValue).ThenBy(b => b.Title)
            .Select(b => new
            {
                b.Id, b.Title, b.FirstPublishYear, b.CoverId, b.OpenLibraryWorkKey,
                b.Subjects, b.Series, b.SeriesPosition,
                Owned = b.ManuallyOwned || b.OwnedDifferentEdition || b.LocalFiles.Any()
            })
            .ToListAsync(ct);

        var entries = books.Select(b =>
        {
            var entry = new XElement(Atom + "entry",
                new XElement(Atom + "id", $"thelibrary:book:{b.Id}"),
                new XElement(Atom + "title", b.Title),
                new XElement(Atom + "updated", DateTime.UtcNow.ToString("o")),
                new XElement(Atom + "author", new XElement(Atom + "name", author.Name)));

            if (b.FirstPublishYear.HasValue)
                entry.Add(new XElement(Dc + "issued", b.FirstPublishYear.Value.ToString()));

            if (b.CoverId.HasValue)
                entry.Add(new XElement(Atom + "link",
                    new XAttribute("rel", "http://opds-spec.org/image/thumbnail"),
                    new XAttribute("href", $"https://covers.openlibrary.org/b/id/{b.CoverId}-M.jpg"),
                    new XAttribute("type", "image/jpeg")));

            if (!string.IsNullOrWhiteSpace(b.Subjects))
            {
                foreach (var subj in b.Subjects.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(5))
                    entry.Add(new XElement(Atom + "category", new XAttribute("term", subj), new XAttribute("label", subj)));
            }

            // Manually-added books (synthetic "XX" keys) have no OpenLibrary
            // page, so the alternate link is omitted for them.
            if (!ManualWorkKey.IsManual(b.OpenLibraryWorkKey))
                entry.Add(new XElement(Atom + "link",
                    new XAttribute("rel", "alternate"),
                    new XAttribute("href", $"https://openlibrary.org/works/{b.OpenLibraryWorkKey}"),
                    new XAttribute("type", "text/html"),
                    new XAttribute("title", "OpenLibrary")));

            var summary = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(b.Series?.Name))
                summary.Append($"Series: {b.Series!.Name}");
            if (!string.IsNullOrWhiteSpace(b.SeriesPosition))
                summary.Append($" #{b.SeriesPosition}");
            summary.Append(b.Owned ? " · Owned" : " · Missing");
            entry.Add(new XElement(Atom + "summary", summary.ToString().Trim(' ', '·')));

            return entry;
        });

        return Xml(BuildFeed($"thelibrary:author:{id}", author.Name, entries));
    }

    [HttpGet("missing.xml")]
    public async Task<ContentResult> Missing(CancellationToken ct)
    {
        var books = await _db.Books.AsNoTracking()
            .Where(b => b.Author.Priority >= 1).Where(BookOwnership.NotOwned)
            .OrderByDescending(b => b.Wanted)
            .ThenByDescending(b => b.Author.Priority)
            .ThenBy(b => b.Author.Name)
            .ThenBy(b => b.Title)
            .Select(b => new
            {
                b.Id, b.Title, b.FirstPublishYear, b.CoverId, b.OpenLibraryWorkKey,
                b.Subjects, b.Wanted,
                AuthorName = b.Author.Name
            })
            .Take(200)
            .ToListAsync(ct);

        var entries = books.Select(b =>
        {
            var entry = new XElement(Atom + "entry",
                new XElement(Atom + "id", $"thelibrary:book:{b.Id}"),
                new XElement(Atom + "title", b.Wanted ? $"★ {b.Title}" : b.Title),
                new XElement(Atom + "updated", DateTime.UtcNow.ToString("o")),
                new XElement(Atom + "author", new XElement(Atom + "name", b.AuthorName)));

            if (b.FirstPublishYear.HasValue)
                entry.Add(new XElement(Dc + "issued", b.FirstPublishYear.Value.ToString()));

            if (b.CoverId.HasValue)
                entry.Add(new XElement(Atom + "link",
                    new XAttribute("rel", "http://opds-spec.org/image/thumbnail"),
                    new XAttribute("href", $"https://covers.openlibrary.org/b/id/{b.CoverId}-M.jpg"),
                    new XAttribute("type", "image/jpeg")));

            if (!ManualWorkKey.IsManual(b.OpenLibraryWorkKey))
                entry.Add(new XElement(Atom + "link",
                    new XAttribute("rel", "alternate"),
                    new XAttribute("href", $"https://openlibrary.org/works/{b.OpenLibraryWorkKey}"),
                    new XAttribute("type", "text/html")));

            return entry;
        });

        return Xml(BuildFeed("thelibrary:missing", "Missing Works", entries));
    }

    [HttpGet("recent.xml")]
    public async Task<ContentResult> Recent(CancellationToken ct)
    {
        var cutoffYear = DateTime.UtcNow.Year - 5;
        var books = await _db.Books.AsNoTracking()
            .Where(b => b.Author.Priority >= 1
                && b.FirstPublishYear >= cutoffYear
                && (b.NormalizedTitle == null || !_db.Books.Any(b2 =>
                    b2.AuthorId == b.AuthorId && b2.NormalizedTitle == b.NormalizedTitle
                    && b2.Id != b.Id && b2.FirstPublishYear < b.FirstPublishYear)))
            .OrderByDescending(b => b.FirstPublishYear)
            .ThenBy(b => b.Title)
            .Take(200)
            .Select(b => new
            {
                b.Id, b.Title, b.FirstPublishYear, b.CoverId, b.OpenLibraryWorkKey,
                AuthorName = b.Author.Name,
                Owned = b.ManuallyOwned || b.OwnedDifferentEdition || b.LocalFiles.Any()
            })
            .ToListAsync(ct);

        var entries = books.Select(b =>
        {
            var entry = new XElement(Atom + "entry",
                new XElement(Atom + "id", $"thelibrary:book:{b.Id}"),
                new XElement(Atom + "title", b.Title),
                new XElement(Atom + "updated", DateTime.UtcNow.ToString("o")),
                new XElement(Atom + "author", new XElement(Atom + "name", b.AuthorName)));

            if (b.FirstPublishYear.HasValue)
                entry.Add(new XElement(Dc + "issued", b.FirstPublishYear.Value.ToString()));

            if (b.CoverId.HasValue)
                entry.Add(new XElement(Atom + "link",
                    new XAttribute("rel", "http://opds-spec.org/image/thumbnail"),
                    new XAttribute("href", $"https://covers.openlibrary.org/b/id/{b.CoverId}-M.jpg"),
                    new XAttribute("type", "image/jpeg")));

            if (!ManualWorkKey.IsManual(b.OpenLibraryWorkKey))
                entry.Add(new XElement(Atom + "link",
                    new XAttribute("rel", "alternate"),
                    new XAttribute("href", $"https://openlibrary.org/works/{b.OpenLibraryWorkKey}"),
                    new XAttribute("type", "text/html")));

            entry.Add(new XElement(Atom + "summary", b.Owned ? "Owned" : "Missing"));
            return entry;
        });

        return Xml(BuildFeed("thelibrary:recent", "Recent Releases", entries));
    }

    private static XElement BuildFeed(string id, string title, IEnumerable<XElement> entries)
        => new(Atom + "feed",
            new XAttribute(XNamespace.Xmlns + "dc", Dc),
            new XAttribute(XNamespace.Xmlns + "opds", OpdsNs),
            new XElement(Atom + "id", id),
            new XElement(Atom + "title", title),
            new XElement(Atom + "updated", DateTime.UtcNow.ToString("o")),
            entries);

    private static XElement NavEntry(string id, string title, string content, string href)
        => new(Atom + "entry",
            new XElement(Atom + "id", id),
            new XElement(Atom + "title", title),
            new XElement(Atom + "updated", DateTime.UtcNow.ToString("o")),
            new XElement(Atom + "content", new XAttribute("type", "text"), content),
            new XElement(Atom + "link",
                new XAttribute("rel", "subsection"),
                new XAttribute("href", href),
                new XAttribute("type", "application/atom+xml;profile=opds-catalog;kind=acquisition")));

    private ContentResult Xml(XElement feed)
    {
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), feed);
        return Content(doc.ToString(), "application/atom+xml;charset=utf-8");
    }
}

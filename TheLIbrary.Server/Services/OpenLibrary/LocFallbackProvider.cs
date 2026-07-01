using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using System.Xml.Linq;
using TheLibrary.Server.Data.Models;

namespace TheLibrary.Server.Services.OpenLibrary;

// Library of Congress as a FREE fallback (no key). Queries the LC catalogue over
// SRU (Search/Retrieve via URL) by ISBN and reads the MODS record it returns. LC's
// strength is traditional US-published books — including older / out-of-print titles
// the other sources miss — which is exactly the bulk of the unresolved 978 tail.
// Gated on an on/off flag rather than a credential (there's nothing to authenticate).
public sealed class LocFallbackProvider : IIsbnFallbackProvider
{
    public const string HttpClientName = "loc";

    private static readonly XNamespace Zs = "http://www.loc.gov/zing/srw/";
    private static readonly XNamespace Mods = "http://www.loc.gov/mods/v3";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<LocFallbackProvider> _log;
    private readonly IsbnSourceThrottle _throttle = new(TimeSpan.FromMilliseconds(1100));

    public LocFallbackProvider(IHttpClientFactory httpFactory, ILogger<LocFallbackProvider> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    public string Name => "Library of Congress";
    // Reuses the credential slot as an on/off flag: a non-blank value ("true") = on.
    public string CredentialSettingKey => AppSettingKeys.LocEnabled;

    public async Task<IsbnLookupResult> LookupAsync(string isbn, string? credential, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(credential)) return IsbnLookupResult.Skipped; // not enabled

        await _throttle.WaitAsync(ct);
        var query = HttpUtility.UrlEncode($"bath.isbn={isbn}");
        var url = $"lcdb?version=1.1&operation=searchRetrieve&query={query}&maximumRecords=1&recordSchema=mods";
        var http = _httpFactory.CreateClient(HttpClientName);

        HttpResponseMessage resp;
        try { resp = await http.GetAsync(url, ct); }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "Library of Congress lookup for {Isbn} failed transiently", isbn);
            return IsbnLookupResult.Unavailable;
        }
        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.TooManyRequests) return IsbnLookupResult.Unavailable;
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Library of Congress returned HTTP {Status} for {Isbn}", (int)resp.StatusCode, isbn);
                return IsbnLookupResult.Unavailable;
            }

            var xml = await resp.Content.ReadAsStringAsync(ct);
            XDocument doc;
            try { doc = XDocument.Parse(xml); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Library of Congress returned unparseable XML for {Isbn}", isbn);
                return IsbnLookupResult.Miss;
            }

            var count = (int?)doc.Descendants(Zs + "numberOfRecords").FirstOrDefault();
            if (count is null or 0) return IsbnLookupResult.Miss;

            var mods = doc.Descendants(Mods + "mods").FirstOrDefault();
            if (mods is null) return IsbnLookupResult.Miss;

            // Main title = the titleInfo with no `type` (skip alternative/abbreviated),
            // nonSort ("The ") + title.
            var titleInfo = mods.Elements(Mods + "titleInfo").FirstOrDefault(e => e.Attribute("type") is null);
            var raw = ((string?)titleInfo?.Element(Mods + "nonSort") ?? "")
                    + ((string?)titleInfo?.Element(Mods + "title") ?? "");
            var title = Collapse(raw);
            if (string.IsNullOrWhiteSpace(title)) return IsbnLookupResult.Miss;

            // Primary personal name, else the first personal name; take the namePart
            // that isn't the date part.
            var name = mods.Elements(Mods + "name")
                    .FirstOrDefault(e => (string?)e.Attribute("type") == "personal" && (string?)e.Attribute("usage") == "primary")
                ?? mods.Elements(Mods + "name")
                    .FirstOrDefault(e => (string?)e.Attribute("type") == "personal");
            var author = CleanName((string?)name?.Elements(Mods + "namePart")
                .FirstOrDefault(e => e.Attribute("type") is null));

            var year = ParseYear(mods.Descendants(Mods + "dateIssued")
                .Select(e => e.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)));

            return IsbnLookupResult.Found(title, author, year);
        }
    }

    private static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    // LC names are "Last, First (Full Form), 1900-1990," — drop the parenthetical and
    // trailing punctuation. Kept in "Last, First" order (the author matcher normalizes
    // that, and it's still readable).
    private static string? CleanName(string? n)
    {
        if (string.IsNullOrWhiteSpace(n)) return null;
        var s = n.Trim();
        var paren = s.IndexOf('(');
        if (paren > 0) s = s[..paren];
        s = s.Trim().TrimEnd(',').Trim();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static int? ParseYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date)) return null;
        var m = Regex.Match(date, @"\d{4}");
        return m.Success && int.TryParse(m.Value, out var y) && y is > 0 and < 3000 ? y : null;
    }
}

using System.Net;
using System.Text.RegularExpressions;
using RtfPipe;

namespace TheLibrary.Server.Services.Calibre;

// Converts RTF to plain text. RtfPipe handles the actual RTF grammar (control
// words, groups, unicode escapes, code pages) by producing HTML; we then strip
// the markup to readable text. Used to preview an RTF as text and to page-count
// it in the integrity check — no Calibre needed.
public static class RtfTextExtractor
{
    static RtfTextExtractor()
    {
        // RtfPipe decodes legacy code pages (e.g. Windows-1252) which aren't
        // registered by default on .NET Core+. Without this its static init
        // throws a TypeInitializationException on the first conversion.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    private static readonly Regex TagRx = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex BlockEndRx = new(
        @"</(p|div|h[1-6]|li|tr)\s*>|<br\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex InlineWsRx = new(@"[^\S\n]+", RegexOptions.Compiled);
    private static readonly Regex TrailingSpaceRx = new(@"[^\S\n]*\n", RegexOptions.Compiled);
    private static readonly Regex ManyNewlinesRx = new(@"\n{3,}", RegexOptions.Compiled);

    public static string ExtractText(string rtf)
    {
        if (string.IsNullOrWhiteSpace(rtf)) return "";

        string html;
        try { html = Rtf.ToHtml(rtf); }
        catch { return ""; }
        if (string.IsNullOrEmpty(html)) return "";

        // Preserve paragraph/line boundaries as newlines before stripping tags.
        html = BlockEndRx.Replace(html, "\n");
        var text = WebUtility.HtmlDecode(TagRx.Replace(html, ""));

        // Collapse intra-line whitespace, trim trailing spaces, cap blank runs.
        text = InlineWsRx.Replace(text, " ");
        text = TrailingSpaceRx.Replace(text, "\n");
        text = ManyNewlinesRx.Replace(text, "\n\n");
        return text.Trim();
    }

    public static string ExtractText(Stream rtfStream)
    {
        using var reader = new StreamReader(rtfStream);
        return ExtractText(reader.ReadToEnd());
    }
}

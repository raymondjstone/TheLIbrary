using TheLibrary.Server.Services.Calibre;
using Xunit;

namespace TheLibrary.Server.Tests;

public class RtfTextExtractorTests
{
    [Fact]
    public void ExtractText_Returns_Readable_Text_From_Rtf()
    {
        const string rtf = @"{\rtf1\ansi\deff0 Hello world\par This is the second line.\par}";

        var text = RtfTextExtractor.ExtractText(rtf);

        Assert.Contains("Hello world", text);
        Assert.Contains("This is the second line.", text);
        // The RTF control words must not leak into the output.
        Assert.DoesNotContain(@"\rtf", text);
        Assert.DoesNotContain(@"\par", text);
    }

    [Fact]
    public void ExtractText_Returns_Empty_For_Blank_Input()
    {
        Assert.Equal("", RtfTextExtractor.ExtractText(""));
        Assert.Equal("", RtfTextExtractor.ExtractText("   "));
    }

    [Fact]
    public void ExtractText_Does_Not_Throw_On_Garbage()
    {
        // Not valid RTF — must degrade to empty/best-effort, never throw.
        var text = RtfTextExtractor.ExtractText("this is not rtf at all");
        Assert.NotNull(text);
    }
}

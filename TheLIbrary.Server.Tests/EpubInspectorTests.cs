using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

public class EpubInspectorTests
{
    [Fact]
    public void Inspect_Valid_Epub_Estimates_Pages_From_Text_Length()
    {
        // 25 600 chars / 1024 = 25 estimated pages, across two spine docs.
        var bytes = TestEpub.Build(
            ("ch1.xhtml", TestEpub.HtmlWithText(12_800)),
            ("ch2.xhtml", TestEpub.HtmlWithText(12_800)));

        var result = EpubInspector.Inspect(new MemoryStream(bytes));

        Assert.True(result.Valid);
        Assert.Null(result.Error);
        Assert.Equal(25, result.EstimatedPages);
    }

    [Fact]
    public void Inspect_Strips_Markup_When_Counting_Text()
    {
        // Lots of tags, little actual text — must not inflate the page estimate.
        var html = "<html><body>" + string.Concat(Enumerable.Repeat("<p><span></span></p>", 500)) + "hello</body></html>";
        var bytes = TestEpub.Build(("ch1.xhtml", html));

        var result = EpubInspector.Inspect(new MemoryStream(bytes));

        Assert.True(result.Valid);
        Assert.True(result.TextChars < 50); // "hello" plus collapsed whitespace, not thousands
    }

    [Fact]
    public void Inspect_Rejects_Non_Zip_Stream()
    {
        var result = EpubInspector.Inspect(new MemoryStream(new byte[] { 1, 2, 3, 4, 5 }));

        Assert.False(result.Valid);
        Assert.Contains("not a valid epub", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inspect_Rejects_Epub_Missing_Container()
    {
        var bytes = TestEpub.Build(includeContainer: false, includeSpine: true,
            ("ch1.xhtml", TestEpub.HtmlWithText(30_000)));

        var result = EpubInspector.Inspect(new MemoryStream(bytes));

        Assert.False(result.Valid);
        Assert.Contains("container.xml", result.Error!);
    }

    [Fact]
    public void Inspect_Rejects_Epub_With_Empty_Spine()
    {
        var bytes = TestEpub.Build(includeContainer: true, includeSpine: false,
            ("ch1.xhtml", TestEpub.HtmlWithText(30_000)));

        var result = EpubInspector.Inspect(new MemoryStream(bytes));

        Assert.False(result.Valid);
        Assert.Contains("spine", result.Error!, StringComparison.OrdinalIgnoreCase);
    }
}

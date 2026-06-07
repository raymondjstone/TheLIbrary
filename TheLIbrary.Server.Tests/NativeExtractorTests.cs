using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

// The Calibre-free extractors used by the integrity check (and available for
// previews): FB2 / DOCX / ODT text, and CBZ/CBR image counting.
public class NativeExtractorTests
{
    [Fact]
    public void Fb2_Extracts_Body_Text()
    {
        var text = Fb2TextExtractor.ExtractText(new MemoryStream(TestDocs.Fb2(5000)));
        Assert.True(text.Length >= 5000, $"got {text.Length}");
        Assert.DoesNotContain("FictionBook", text);
        Assert.DoesNotContain("<p>", text);
    }

    [Fact]
    public void Docx_Extracts_Run_Text()
    {
        var text = OfficeTextExtractor.ExtractDocx(new MemoryStream(TestDocs.Docx(5000)));
        Assert.True(text.Length >= 5000, $"got {text.Length}");
        Assert.DoesNotContain("w:t", text);
    }

    [Fact]
    public void Odt_Extracts_Paragraph_Text()
    {
        var text = OfficeTextExtractor.ExtractOdt(new MemoryStream(TestDocs.Odt(5000)));
        Assert.True(text.Length >= 5000, $"got {text.Length}");
        Assert.DoesNotContain("text:p", text);
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3 })]            // not a zip/rar at all
    public void Extractors_Return_Empty_On_Garbage(byte[] garbage)
    {
        Assert.Equal("", OfficeTextExtractor.ExtractDocx(new MemoryStream(garbage)));
        Assert.Equal("", OfficeTextExtractor.ExtractOdt(new MemoryStream(garbage)));
        Assert.Equal("", Fb2TextExtractor.ExtractText(new MemoryStream(garbage)));
    }

    [Fact]
    public void ComicArchive_Counts_Only_Image_Entries()
    {
        // 25 jpgs + one ComicInfo.xml that must not count.
        var count = ComicArchive.CountImages(new MemoryStream(TestDocs.Cbz(25)));
        Assert.Equal(25, count);
    }

    [Fact]
    public void ComicArchive_Returns_Negative_For_Unreadable_Archive()
    {
        Assert.True(ComicArchive.CountImages(new MemoryStream(new byte[] { 9, 9, 9, 9 })) < 0);
    }
}

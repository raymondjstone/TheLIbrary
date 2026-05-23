using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class FilePreviewResolverMoreTests
{
    private static readonly IReadOnlyList<string> Roots = new[]
    {
        Path.Combine(Path.GetTempPath(), "thelibrary-preview-root-a"),
        Path.Combine(Path.GetTempPath(), "thelibrary-preview-root-b")
    };

    [Fact]
    public void Resolve_Uses_CaseInsensitive_Extension_Match_For_Single_File()
    {
        var path = Path.Combine(Roots[0], "book.EPUB");

        var result = FilePreviewResolver.Resolve(path, "epub", Roots, _ => Array.Empty<string>());

        Assert.NotNull(result.Ok);
        Assert.EndsWith("book.EPUB", result.Ok!.FullPath);
    }

    [Fact]
    public void Resolve_Uses_CaseInsensitive_Extension_Match_When_Enumerating_Directory()
    {
        var dir = Path.Combine(Roots[0], "Book Folder");

        var result = FilePreviewResolver.Resolve(dir, "pdf", Roots, _ => new[]
        {
            Path.Combine(dir, "book.PDF")
        });

        Assert.NotNull(result.Ok);
        Assert.EndsWith("book.PDF", result.Ok!.FullPath);
    }

    [Fact]
    public void Resolve_Returns_First_Matching_File_From_Enumeration()
    {
        var dir = Path.Combine(Roots[0], "Book Folder");

        var result = FilePreviewResolver.Resolve(dir, "epub", Roots, _ => new[]
        {
            Path.Combine(dir, "b.epub"),
            Path.Combine(dir, "a.epub")
        });

        Assert.NotNull(result.Ok);
        Assert.EndsWith("b.epub", result.Ok!.FullPath);
    }

    [Fact]
    public void Resolve_Returns_NoMatchingFile_When_Enumerator_Throws()
    {
        var dir = Path.Combine(Roots[0], "Book Folder");

        var result = FilePreviewResolver.Resolve(dir, "epub", Roots, _ => throw new IOException("boom"));

        Assert.Null(result.Ok);
        Assert.Equal(FilePreviewResolver.FailureKind.NoMatchingFile, result.Failure);
    }

    [Fact]
    public void Resolve_Returns_OutsideLibrary_When_Enumerated_File_Escapes_Root()
    {
        var dir = Path.Combine(Roots[0], "Book Folder");
        var outside = Path.Combine(Path.GetTempPath(), "somewhere-else", "book.epub");

        var result = FilePreviewResolver.Resolve(dir, "epub", Roots, _ => new[] { outside });

        Assert.Null(result.Ok);
        Assert.Equal(FilePreviewResolver.FailureKind.OutsideLibrary, result.Failure);
    }

    [Fact]
    public void Resolve_Returns_Correct_Text_ContentType()
    {
        var path = Path.Combine(Roots[0], "notes.txt");

        var result = FilePreviewResolver.Resolve(path, "txt", Roots, _ => Array.Empty<string>());

        Assert.Equal("text/plain; charset=utf-8", result.Ok!.ContentType);
    }

    [Fact]
    public void IsInsideAnyRoot_Ignores_Blank_Root_Entries()
    {
        var path = Path.Combine(Roots[0], "book.epub");
        var roots = new[] { "", "   ", Roots[0] };

        Assert.True(FilePreviewResolver.IsInsideAnyRoot(path, roots));
    }

    [Fact]
    public void IsInsideAnyRoot_Is_CaseInsensitive()
    {
        var root = Roots[0].ToUpperInvariant();
        var path = Path.Combine(Roots[0].ToLowerInvariant(), "book.epub");

        Assert.True(FilePreviewResolver.IsInsideAnyRoot(path, new[] { root }));
    }
}

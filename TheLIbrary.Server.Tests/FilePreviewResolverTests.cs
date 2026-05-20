using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

// Covers FilePreviewResolver's two responsibilities: pick the right physical
// file for a (storedPath, format) pair, and refuse to resolve anything that
// lives outside an enabled library root.
public class FilePreviewResolverTests
{
    private static readonly IReadOnlyList<string> Roots = new[]
    {
        Path.Combine(Path.GetTempPath(), "thelibrary-test-root"),
        Path.Combine(Path.GetTempPath(), "another-root"),
    };

    // ── Supported-format gate ────────────────────────────────────────────────

    [Theory]
    [InlineData("epub")]
    [InlineData("pdf")]
    [InlineData("txt")]
    [InlineData("EPUB")]    // case-insensitive
    public void Accepts_supported_format(string fmt)
    {
        var path = Path.Combine(Roots[0], $"book.{fmt.ToLower()}");
        var r = FilePreviewResolver.Resolve(path, fmt, Roots,
            _ => Array.Empty<string>());
        Assert.NotNull(r.Ok);
        Assert.Null(r.Failure);
    }

    [Theory]
    [InlineData("mobi")]
    [InlineData("lit")]
    [InlineData("azw3")]
    [InlineData("")]
    public void Rejects_unsupported_format(string fmt)
    {
        var path = Path.Combine(Roots[0], $"book.{fmt}");
        var r = FilePreviewResolver.Resolve(path, fmt, Roots,
            _ => Array.Empty<string>());
        Assert.Null(r.Ok);
        Assert.Equal(FilePreviewResolver.FailureKind.UnsupportedFormat, r.Failure);
    }

    // ── Single-file storedPath: extension must match the requested format ───

    [Fact]
    public void Matching_single_file_returns_path_unchanged()
    {
        var path = Path.Combine(Roots[0], "book.epub");
        var r = FilePreviewResolver.Resolve(path, "epub", Roots,
            _ => Array.Empty<string>());

        Assert.NotNull(r.Ok);
        Assert.Equal(Path.GetFullPath(path), r.Ok!.FullPath);
        Assert.Equal("application/epub+zip", r.Ok.ContentType);
        Assert.Equal("book.epub", r.Ok.FileName);
    }

    [Fact]
    public void Mismatched_single_file_extension_falls_through_to_directory_enumeration()
    {
        // storedPath ends in .mobi, but the user asked for epub; the resolver
        // treats storedPath as a directory and looks for an .epub inside it.
        var dir = Path.Combine(Roots[0], "Some Book");
        var inside = Path.Combine(dir, "Some Book.epub");
        var r = FilePreviewResolver.Resolve(
            Path.Combine(dir, "Some Book.mobi"),  // mobi != epub → try directory
            "epub", Roots,
            _ => new[] { inside });

        // The enumerator is called with the storedPath, which isn't a directory
        // in this scenario — but the fake enumerator returns inside anyway, so
        // we get back the inside path. The point of the test is: a mismatched
        // extension doesn't short-circuit to "not found".
        Assert.NotNull(r.Ok);
        Assert.EndsWith("Some Book.epub", r.Ok!.FullPath);
    }

    [Fact]
    public void Directory_with_no_matching_format_returns_NoMatchingFile()
    {
        var dir = Path.Combine(Roots[0], "Some Book");
        var r = FilePreviewResolver.Resolve(dir, "epub", Roots,
            _ => new[] { Path.Combine(dir, "Some Book.mobi") });

        Assert.Null(r.Ok);
        Assert.Equal(FilePreviewResolver.FailureKind.NoMatchingFile, r.Failure);
    }

    [Fact]
    public void Directory_with_multiple_formats_picks_the_requested_one()
    {
        var dir = Path.Combine(Roots[0], "Some Book");
        var r = FilePreviewResolver.Resolve(dir, "pdf", Roots,
            _ => new[] {
                Path.Combine(dir, "Some Book.epub"),
                Path.Combine(dir, "Some Book.pdf"),
                Path.Combine(dir, "Some Book.mobi"),
            });

        Assert.NotNull(r.Ok);
        Assert.EndsWith(".pdf", r.Ok!.FullPath);
        Assert.Equal("application/pdf", r.Ok.ContentType);
    }

    // ── Path-traversal / library-root guard ──────────────────────────────────

    [Fact]
    public void File_outside_any_root_is_rejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), "evil", "secret.epub");
        var r = FilePreviewResolver.Resolve(outside, "epub", Roots,
            _ => Array.Empty<string>());

        Assert.Null(r.Ok);
        Assert.Equal(FilePreviewResolver.FailureKind.OutsideLibrary, r.Failure);
    }

    [Fact]
    public void Path_with_double_dots_still_canonicalises_into_a_root_when_legitimately_inside()
    {
        // <root>/sub/../file.epub canonicalises to <root>/file.epub — that IS
        // inside the root, so it should pass.
        var sneaky = Path.Combine(Roots[0], "sub", "..", "file.epub");
        var r = FilePreviewResolver.Resolve(sneaky, "epub", Roots,
            _ => Array.Empty<string>());

        Assert.NotNull(r.Ok);
        Assert.Equal(Path.GetFullPath(Path.Combine(Roots[0], "file.epub")), r.Ok!.FullPath);
    }

    [Fact]
    public void Path_with_double_dots_escaping_root_is_rejected()
    {
        // <root>/../sibling/secret.txt canonicalises out of the root → reject.
        // The file is a single-file storedPath whose extension matches the
        // requested format, so we reach the library-root check.
        var escape = Path.Combine(Roots[0], "..", "sibling", "secret.txt");
        var r = FilePreviewResolver.Resolve(escape, "txt", Roots,
            _ => Array.Empty<string>());

        Assert.Null(r.Ok);
        Assert.Equal(FilePreviewResolver.FailureKind.OutsideLibrary, r.Failure);
    }

    [Fact]
    public void Root_prefix_collision_is_not_a_false_positive()
    {
        // "/books/Coll" and "/books/Collection" share a prefix but are distinct
        // roots. A file in "/books/Collection2" must NOT match a root of
        // "/books/Coll".
        var fakeRoots = new[] { Path.Combine(Path.GetTempPath(), "rootA") };
        var path = Path.Combine(Path.GetTempPath(), "rootAB", "file.epub");
        Assert.False(FilePreviewResolver.IsInsideAnyRoot(path, fakeRoots));
    }

    [Fact]
    public void Root_itself_counts_as_inside()
    {
        Assert.True(FilePreviewResolver.IsInsideAnyRoot(Roots[0], Roots));
    }
}

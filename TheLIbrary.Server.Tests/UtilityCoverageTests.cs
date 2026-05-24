using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Services.Incoming;
using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class UtilityCoverageTests
{
    [Fact]
    public void ExceptionFormatter_Flattens_Exception_Chain_In_Order()
    {
        var ex = new InvalidOperationException("outer", new Exception("inner"));

        var flat = ExceptionFormatter.Flatten(ex);

        Assert.Equal("[InvalidOperationException] outer → [Exception] inner", flat);
    }

    [Fact]
    public void ExceptionFormatter_Formats_DbUpdateException_With_Type_Name()
    {
        var ex = new DbUpdateException("db failed", new Exception("root cause"));

        var flat = ExceptionFormatter.Flatten(ex);

        Assert.StartsWith("[DbUpdateException] db failed", flat);
        Assert.Contains("[Exception] root cause", flat);
    }

    [Fact]
    public void ExceptionFormatter_Stops_After_Six_Levels()
    {
        Exception ex = new Exception("6");
        ex = new Exception("5", ex);
        ex = new Exception("4", ex);
        ex = new Exception("3", ex);
        ex = new Exception("2", ex);
        ex = new Exception("1", ex);
        ex = new Exception("0", ex);

        var flat = ExceptionFormatter.Flatten(ex);

        Assert.DoesNotContain("[Exception] 6", flat);
        Assert.Equal(6, flat.Split(" → ").Length);
    }

    [Fact]
    public void FilePreviewResolver_Empty_StoredPath_Returns_NoMatchingFile()
    {
        var result = FilePreviewResolver.Resolve("", "epub", new[] { Path.GetTempPath() }, _ => Array.Empty<string>());

        Assert.Null(result.Ok);
        Assert.Equal(FilePreviewResolver.FailureKind.NoMatchingFile, result.Failure);
    }

    [Fact]
    public void FilePreviewResolver_Whitespace_Path_Is_Not_Inside_Any_Root()
    {
        Assert.False(FilePreviewResolver.IsInsideAnyRoot("   ", new[] { Path.GetTempPath() }));
    }

    [Fact]
    public void FilePreviewResolver_Empty_Root_List_Rejects_Any_Path()
    {
        var path = Path.Combine(Path.GetTempPath(), "book.epub");

        Assert.False(FilePreviewResolver.IsInsideAnyRoot(path, Array.Empty<string>()));
    }

    [Fact]
    public void ManualWorkKey_IsManual_Is_True_For_Any_XX_Prefix()
    {
        Assert.True(ManualWorkKey.IsManual("XXnot-a-real-shape"));
    }

    [Fact]
    public void AuthorMatcher_TryGet_Returns_Null_For_Blank_Input()
    {
        var matcher = new AuthorMatcher(new[] { new AuthorIndexEntry("Lena Hart", "Lena Hart", true) });

        Assert.Null(matcher.TryGet(null));
        Assert.Null(matcher.TryGet("   "));
    }

    [Fact]
    public void AuthorMatcher_ResolveFolderAncestor_Stops_At_SourceRoot()
    {
        var matcher = new AuthorMatcher(new[] { new AuthorIndexEntry("Mira C. Rowan", "Mira C. Rowan", true) });

        var hit = matcher.ResolveFolderAncestor(
            folderPath: @"X:\drop",
            sourceRoot: @"X:\drop");

        Assert.Null(hit);
    }

    [Fact]
    public void AuthorMatcher_ResolveFolderAncestor_Finds_Tracked_Author_Above_Title_Folder()
    {
        var matcher = new AuthorMatcher(new[] { new AuthorIndexEntry("Mira C. Rowan", "Mira C. Rowan", true, 1) });

        var hit = matcher.ResolveFolderAncestor(
            folderPath: @"X:\drop\Mira C. Rowan\Signal over Haven\formats",
            sourceRoot: @"X:\drop");

        Assert.NotNull(hit);
        Assert.Equal(1, hit!.TrackedAuthorId);
    }

    [Fact]
    public void AuthorMatcher_ResolveFolderAncestor_Skips_Unknown_Folder_Name()
    {
        var matcher = new AuthorMatcher(new[]
        {
            new AuthorIndexEntry("__unknown", "__unknown", true, 1),
            new AuthorIndexEntry("Mira C. Rowan", "Mira C. Rowan", true, 2),
        });

        var hit = matcher.ResolveFolderAncestor(
            folderPath: @"X:\drop\__unknown\Mira C. Rowan\Book",
            sourceRoot: @"X:\drop");

        Assert.NotNull(hit);
        Assert.Equal(2, hit!.TrackedAuthorId);
    }
}

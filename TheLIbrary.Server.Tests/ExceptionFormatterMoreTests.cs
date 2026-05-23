using Microsoft.EntityFrameworkCore;
using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class ExceptionFormatterMoreTests
{
    [Fact]
    public void Flatten_Formats_Single_Exception()
    {
        var ex = new InvalidOperationException("bad things happened");

        var result = ExceptionFormatter.Flatten(ex);

        Assert.Equal("[InvalidOperationException] bad things happened", result);
    }

    [Fact]
    public void Flatten_Preserves_Arrow_Order_For_Three_Levels()
    {
        var ex = new InvalidOperationException("outer",
            new ArgumentException("middle",
                new Exception("inner")));

        var result = ExceptionFormatter.Flatten(ex);

        Assert.Equal("[InvalidOperationException] outer → [ArgumentException] middle → [Exception] inner", result);
    }

    [Fact]
    public void Flatten_Trims_Whitespace_In_Messages()
    {
        var ex = new Exception("  spaced message  ");

        var result = ExceptionFormatter.Flatten(ex);

        Assert.Equal("[Exception] spaced message", result);
    }

    [Fact]
    public void Flatten_Formats_DbUpdateException_Without_StackDump()
    {
        var ex = new DbUpdateException(" update failed ");

        var result = ExceptionFormatter.Flatten(ex);

        Assert.Equal("[DbUpdateException] update failed", result);
    }
}

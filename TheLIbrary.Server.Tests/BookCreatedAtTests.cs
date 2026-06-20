using TheLibrary.Server.Data.Models;
using Xunit;

namespace TheLibrary.Server.Tests;

public class BookCreatedAtTests
{
    // A book first seen with a PAST publish year is dated to 1 Jan of that year,
    // so an old title doesn't masquerade as a new release in the Recent Releases
    // by-month grouping.
    [Fact]
    public void PastPublishYear_Dates_To_Jan1_Of_That_Year()
    {
        var result = Book.CreatedAtForPublishYear(2019);
        Assert.Equal(new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc), result);
    }

    // A book published THIS year genuinely is new now → null, so the DB default
    // (SYSUTCDATETIME) stamps the real insert time.
    [Fact]
    public void CurrentPublishYear_Returns_Null_To_Use_Insert_Time()
    {
        Assert.Null(Book.CreatedAtForPublishYear(DateTime.UtcNow.Year));
    }

    // Unknown publish year → null (use insert time).
    [Fact]
    public void NullPublishYear_Returns_Null()
    {
        Assert.Null(Book.CreatedAtForPublishYear(null));
    }

    // A future year (OL data error that slipped past clamping) must not produce a
    // future CreatedAt — fall back to the insert time.
    [Fact]
    public void FuturePublishYear_Returns_Null()
    {
        Assert.Null(Book.CreatedAtForPublishYear(DateTime.UtcNow.Year + 2));
    }

    // Guard the DATEFROMPARTS-equivalent lower bound: a nonsense year <= 0 must
    // not throw or produce a bogus date.
    [Fact]
    public void NonPositiveYear_Returns_Null()
    {
        Assert.Null(Book.CreatedAtForPublishYear(0));
        Assert.Null(Book.CreatedAtForPublishYear(-5));
    }
}

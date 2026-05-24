using System.Reflection;
using System.Text.Json;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.OpenLibrary;
using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

public class AuthorRefresherUtilityTests
{
    [Theory]
    [InlineData(0, 2)]
    [InlineData(3, 14)]
    [InlineData(8, 28)]
    [InlineData(20, 60)]
    public void NextFetchInterval_Uses_Expected_Bucket_For_Most_Recent_Year(int ageYears, int expectedDays)
    {
        var years = new[] { DateTime.UtcNow.Year - ageYears };
        var result = AuthorRefresher.NextFetchInterval(years);
        Assert.Equal(TimeSpan.FromDays(expectedDays), result);
    }

    [Fact]
    public void NextFetchInterval_Uses_Most_Recent_Year_When_Multiple_Are_Present()
    {
        var years = new[] { 1980, 2012, DateTime.UtcNow.Year };

        var result = AuthorRefresher.NextFetchInterval(years);

        Assert.Equal(TimeSpan.FromDays(2), result);
    }

    [Fact]
    public void NextFetchInterval_Empty_Years_Uses_Longest_Bucket()
    {
        Assert.Equal(TimeSpan.FromDays(60), AuthorRefresher.NextFetchInterval(Array.Empty<int>()));
    }

    [Fact]
    public void NextFetchInterval_Uses_Configured_Buckets_When_Provided()
    {
        var cadence = new AuthorRefresher.RefreshCadenceSettings(3, 15, 29, 61);

        Assert.Equal(TimeSpan.FromDays(3), AuthorRefresher.NextFetchInterval([DateTime.UtcNow.Year], cadence));
        Assert.Equal(TimeSpan.FromDays(15), AuthorRefresher.NextFetchInterval([DateTime.UtcNow.Year - 3], cadence));
        Assert.Equal(TimeSpan.FromDays(29), AuthorRefresher.NextFetchInterval([DateTime.UtcNow.Year - 8], cadence));
        Assert.Equal(TimeSpan.FromDays(61), AuthorRefresher.NextFetchInterval(Array.Empty<int>(), cadence));
    }

    [Fact]
    public void PickBestAuthor_Returns_Null_When_No_Docs()
    {
        var pick = InvokePickBestAuthor(null, "Terry Brooks");
        Assert.Null(pick);
    }

    [Fact]
    public void PickBestAuthor_Uses_Normalized_Name_Match()
    {
        var docs = new List<AuthorSearchDoc>
        {
            new() { Key = "OL1A", Name = "Someone Else" },
            new() { Key = "OL2A", Name = "Brooks, Terry" },
        };

        var pick = InvokePickBestAuthor(docs, "Terry Brooks");

        Assert.NotNull(pick);
        Assert.Equal("OL2A", pick!.Key);
    }

    [Fact]
    public void PickBestAuthor_Returns_First_Normalized_Match()
    {
        var docs = new List<AuthorSearchDoc>
        {
            new() { Key = "OL1A", Name = "Arthur C. Clarke" },
            new() { Key = "OL2A", Name = "Clarke, Arthur C." },
        };

        var pick = InvokePickBestAuthor(docs, "Arthur C. Clarke");

        Assert.NotNull(pick);
        Assert.Equal("OL1A", pick!.Key);
    }

    [Fact]
    public void PickManualToPromote_Returns_Null_For_Empty_Input_Or_Empty_Title()
    {
        Assert.Null(InvokePickManualToPromote(new List<Book>(), "foundation"));
        Assert.Null(InvokePickManualToPromote(new List<Book> { Manual("Foundation") }, ""));
    }

    [Fact]
    public void PickManualToPromote_Prefers_Single_Exact_Normalized_Title_Match()
    {
        var manual = new List<Book>
        {
            Manual("Second Foundation"),
            Manual("Foundation"),
        };

        var pick = InvokePickManualToPromote(manual, TitleNormalizer.Normalize("Foundation"));

        Assert.Same(manual[1], pick);
    }

    [Fact]
    public void PickManualToPromote_Returns_Null_When_Exact_Match_Is_Ambiguous()
    {
        var manual = new List<Book>
        {
            Manual("Foundation"),
            Manual("Foundation"),
        };

        var pick = InvokePickManualToPromote(manual, TitleNormalizer.Normalize("Foundation"));

        Assert.Null(pick);
    }

    [Fact]
    public void PickManualToPromote_Uses_Single_Clear_Fuzzy_Match()
    {
        var manual = new List<Book>
        {
            Manual("Foundatoin"),
            Manual("Second Foundation"),
        };

        var pick = InvokePickManualToPromote(manual, TitleNormalizer.Normalize("Foundation"));

        Assert.Same(manual[0], pick);
    }

    [Fact]
    public void PickManualToPromote_Returns_Null_When_Best_Fuzzy_Match_Is_Tied()
    {
        var manual = new List<Book>
        {
            Manual("Foundatoin"),
            Manual("Foundatoin"),
        };

        var pick = InvokePickManualToPromote(manual, TitleNormalizer.Normalize("Foundation"));

        Assert.Null(pick);
    }

    [Fact]
    public void BuildSubjects_Returns_Empty_String_When_Subjects_Are_Null_Or_Blank()
    {
        Assert.Equal("", InvokeBuildSubjects(null));
        Assert.Equal("", InvokeBuildSubjects(new List<string> { "  ", "" }));
    }

    [Fact]
    public void BuildSubjects_Trims_Entries_And_Joins_With_Semicolons()
    {
        var result = InvokeBuildSubjects(new List<string> { " Space opera ", "Science fiction", " " });

        Assert.Equal("Space opera;Science fiction", result);
    }

    [Fact]
    public void BuildSubjects_Truncates_To_2000_Characters()
    {
        var longSubject = new string('x', 2100);

        var result = InvokeBuildSubjects(new List<string> { longSubject });

        Assert.Equal(2000, result.Length);
        Assert.All(result, ch => Assert.Equal('x', ch));
    }

    [Fact]
    public void ExtractBio_Reads_String_Value()
    {
        using var doc = JsonDocument.Parse("\"A short bio\"");
        var bio = doc.RootElement;

        Assert.Equal("A short bio", InvokeExtractBio(bio));
    }

    [Fact]
    public void ExtractBio_Reads_Object_Value_Field()
    {
        using var doc = JsonDocument.Parse("{\"value\":\"Nested bio\"}");
        var bio = doc.RootElement;

        Assert.Equal("Nested bio", InvokeExtractBio(bio));
    }

    [Fact]
    public void ExtractBio_Blank_Or_Unsupported_Values_Return_Null()
    {
        using var blankStringDoc = JsonDocument.Parse("\"   \"");
        using var blankObjectDoc = JsonDocument.Parse("{\"value\":\"\"}");
        using var arrayDoc = JsonDocument.Parse("[]");

        Assert.Null(InvokeExtractBio(blankStringDoc.RootElement));
        Assert.Null(InvokeExtractBio(blankObjectDoc.RootElement));
        Assert.Null(InvokeExtractBio(arrayDoc.RootElement));
    }

    private static Book Manual(string title) => new()
    {
        Title = title,
        NormalizedTitle = TitleNormalizer.Normalize(title),
        OpenLibraryWorkKey = ManualWorkKey.NewCandidate()
    };

    private static AuthorSearchDoc? InvokePickBestAuthor(List<AuthorSearchDoc>? docs, string searchName) =>
        (AuthorSearchDoc?)typeof(AuthorRefresher)
            .GetMethod("PickBestAuthor", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object?[] { docs, searchName });

    private static Book? InvokePickManualToPromote(List<Book> manual, string normTitle) =>
        (Book?)typeof(AuthorRefresher)
            .GetMethod("PickManualToPromote", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object?[] { manual, normTitle });

    private static string InvokeBuildSubjects(List<string>? subjects) =>
        (string)typeof(AuthorRefresher)
            .GetMethod("BuildSubjects", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object?[] { subjects })!;

    private static string? InvokeExtractBio(JsonElement bio) =>
        (string?)typeof(AuthorRefresher)
            .GetMethod("ExtractBio", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { bio });
}

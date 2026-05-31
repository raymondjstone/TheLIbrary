using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TheLibrary.Server.Controllers;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

// Integration coverage for the two settings groups added in this session:
//   * /api/settings/openlibrary    — User-Agent identity (app name + email)
//   * /api/settings/refresh-limits — refresh-due-works caps (max/run, early)
[Collection("Integration")]
public class SettingsControllerIntegrationTests
{
    // ── OpenLibrary identity ─────────────────────────────────────────────────

    [Fact]
    public async Task GetOpenLibrary_Returns_Blank_Defaults_On_Fresh_Db()
    {
        using var factory = new LibraryApiFactory();
        using var client = factory.CreateClient();
        var dto = await client.GetFromJsonAsync<SettingsController.OpenLibraryIdentityDto>(
            "/api/settings/openlibrary");
        Assert.NotNull(dto);
        Assert.Equal("", dto!.AppName);
        Assert.Equal("", dto.ContactEmail);
        Assert.False(dto.Identified);
        Assert.Equal("TheLibrary", dto.UserAgent);
    }

    [Fact]
    public async Task PutOpenLibrary_Identified_Persists_And_GET_Sees_It()
    {
        using var factory = new LibraryApiFactory();
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync("/api/settings/openlibrary",
            new SettingsController.UpdateOpenLibraryIdentity("MyApp", "me@example.org"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var dto = await put.Content.ReadFromJsonAsync<SettingsController.OpenLibraryIdentityDto>();
        Assert.True(dto!.Identified);
        Assert.Equal("MyApp (me@example.org)", dto.UserAgent);

        var fresh = await client.GetFromJsonAsync<SettingsController.OpenLibraryIdentityDto>(
            "/api/settings/openlibrary");
        Assert.Equal("MyApp", fresh!.AppName);
        Assert.Equal("me@example.org", fresh.ContactEmail);
        Assert.True(fresh.Identified);
    }

    [Fact]
    public async Task PutOpenLibrary_Trims_Surrounding_Whitespace()
    {
        using var factory = new LibraryApiFactory();
        using var client = factory.CreateClient();
        var put = await client.PutAsJsonAsync("/api/settings/openlibrary",
            new SettingsController.UpdateOpenLibraryIdentity("  MyApp  ", "  me@x.io  "));
        var dto = await put.Content.ReadFromJsonAsync<SettingsController.OpenLibraryIdentityDto>();
        Assert.Equal("MyApp", dto!.AppName);
        Assert.Equal("me@x.io", dto.ContactEmail);
    }

    [Fact]
    public async Task PutOpenLibrary_Rejects_Email_Without_At_Sign()
    {
        using var factory = new LibraryApiFactory();
        using var client = factory.CreateClient();
        var response = await client.PutAsJsonAsync("/api/settings/openlibrary",
            new SettingsController.UpdateOpenLibraryIdentity("MyApp", "not-an-email"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PutOpenLibrary_Blank_Email_Drops_To_Anonymous_Tier()
    {
        using var factory = new LibraryApiFactory();
        using var client = factory.CreateClient();

        // Become identified first…
        await client.PutAsJsonAsync("/api/settings/openlibrary",
            new SettingsController.UpdateOpenLibraryIdentity("App", "x@y.io"));

        // …then clear the email — should go back to anonymous.
        var put = await client.PutAsJsonAsync("/api/settings/openlibrary",
            new SettingsController.UpdateOpenLibraryIdentity("App", ""));
        var dto = await put.Content.ReadFromJsonAsync<SettingsController.OpenLibraryIdentityDto>();
        Assert.False(dto!.Identified);
        Assert.Equal("App", dto.UserAgent);
    }

    // ── Refresh limits ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetRefreshLimits_Returns_Defaults_On_Fresh_Db()
    {
        using var factory = new LibraryApiFactory();
        using var client = factory.CreateClient();
        var dto = await client.GetFromJsonAsync<SettingsController.RefreshLimitsDto>(
            "/api/settings/refresh-limits");
        Assert.Equal(0, dto!.MaxAuthorsPerRun);     // 0 = no cap
        Assert.Equal(200, dto.MaxEarlyWhenNoneDue);
    }

    [Fact]
    public async Task PutRefreshLimits_Persists_And_GET_Reflects_It()
    {
        using var factory = new LibraryApiFactory();
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync("/api/settings/refresh-limits",
            new SettingsController.RefreshLimitsDto(50, 25, 0));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var fresh = await client.GetFromJsonAsync<SettingsController.RefreshLimitsDto>(
            "/api/settings/refresh-limits");
        Assert.Equal(50, fresh!.MaxAuthorsPerRun);
        Assert.Equal(25, fresh.MaxEarlyWhenNoneDue);

        // The values are stored as AppSetting rows under their well-known keys.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var rows = await db.AppSettings
            .Where(s => s.Key == AppSettingKeys.RefreshMaxAuthorsPerRun
                     || s.Key == AppSettingKeys.RefreshEarlyWhenNoneDue)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal("50", rows.Single(r => r.Key == AppSettingKeys.RefreshMaxAuthorsPerRun).Value);
        Assert.Equal("25", rows.Single(r => r.Key == AppSettingKeys.RefreshEarlyWhenNoneDue).Value);
    }

    [Fact]
    public async Task PutRefreshLimits_Rejects_Negative_Values()
    {
        using var factory = new LibraryApiFactory();
        using var client = factory.CreateClient();
        var response = await client.PutAsJsonAsync("/api/settings/refresh-limits",
            new SettingsController.RefreshLimitsDto(-1, 100, 0));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PutRefreshLimits_Accepts_Zero_For_Both()
    {
        using var factory = new LibraryApiFactory();
        using var client = factory.CreateClient();
        var put = await client.PutAsJsonAsync("/api/settings/refresh-limits",
            new SettingsController.RefreshLimitsDto(0, 0, 0));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var dto = await put.Content.ReadFromJsonAsync<SettingsController.RefreshLimitsDto>();
        Assert.Equal(0, dto!.MaxAuthorsPerRun);
        Assert.Equal(0, dto.MaxEarlyWhenNoneDue);
    }

    [Fact]
    public async Task GetRefreshCadence_Returns_Defaults_On_Fresh_Db()
    {
        using var factory = new LibraryApiFactory();
        using var client = factory.CreateClient();
        var dto = await client.GetFromJsonAsync<SettingsController.RefreshCadenceDto>(
            "/api/settings/refresh-cadence");
        Assert.Equal(2, dto!.RecentDays);
        Assert.Equal(14, dto.MidDays);
        Assert.Equal(28, dto.DormantDays);
        Assert.Equal(60, dto.OldOrEmptyDays);
    }

    [Fact]
    public async Task PutRefreshCadence_Persists_And_GET_Reflects_It()
    {
        using var factory = new LibraryApiFactory();
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync("/api/settings/refresh-cadence",
            new SettingsController.RefreshCadenceDto(3, 15, 29, 61));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var fresh = await client.GetFromJsonAsync<SettingsController.RefreshCadenceDto>(
            "/api/settings/refresh-cadence");
        Assert.Equal(3, fresh!.RecentDays);
        Assert.Equal(15, fresh.MidDays);
        Assert.Equal(29, fresh.DormantDays);
        Assert.Equal(61, fresh.OldOrEmptyDays);
    }

    [Fact]
    public async Task PutRefreshCadence_Rejects_NonPositive_Values()
    {
        using var factory = new LibraryApiFactory();
        using var client = factory.CreateClient();
        var response = await client.PutAsJsonAsync("/api/settings/refresh-cadence",
            new SettingsController.RefreshCadenceDto(0, 14, 28, 60));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetDuplicateFormatPreference_Returns_Defaults_On_Fresh_Db()
    {
        using var factory = new LibraryApiFactory();
        using var client = factory.CreateClient();

        var dto = await client.GetFromJsonAsync<SettingsController.DuplicateFormatPreferenceDto>(
            "/api/settings/duplicate-format-preference");

        Assert.Equal(BooksController.DefaultFormatPreference, dto!.Formats);
    }

    [Fact]
    public async Task PutDuplicateFormatPreference_Persists_Cleaned_Order()
    {
        using var factory = new LibraryApiFactory();
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync("/api/settings/duplicate-format-preference",
            new SettingsController.DuplicateFormatPreferenceDto([" pdf ", ".epub", "pdf", "cbz"]));

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var fresh = await client.GetFromJsonAsync<SettingsController.DuplicateFormatPreferenceDto>(
            "/api/settings/duplicate-format-preference");
        Assert.Equal(["pdf", "epub", "cbz"], fresh!.Formats);
    }
}

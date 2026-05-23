using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.IO;
using TheLibrary.Server.Services.Remarkable;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

public class RemarkableClientTests
{
    [Fact]
    public async Task ConnectAsync_Stores_Device_Token()
    {
        await using var db = CreateDb();
        var client = CreateClient(db, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("device-token")
        }));

        var auth = await client.ConnectAsync("  otp123  ", CancellationToken.None);

        Assert.Equal("device-token", auth.DeviceToken);
        Assert.False(string.IsNullOrWhiteSpace(auth.DeviceId));
    }

    [Fact]
    public async Task ConnectAsync_Rejects_Empty_Code()
    {
        await using var db = CreateDb();
        var client = CreateClient(db, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        var ex = await Assert.ThrowsAsync<RemarkableException>(() => client.ConnectAsync(" ", CancellationToken.None));
        Assert.Contains("One-time code is required", ex.Message);
    }

    [Fact]
    public async Task SendFileAsync_Uploads_Epub_And_Updates_LastSent()
    {
        await using var db = CreateDb();
        var auth = new RemarkableAuth
        {
            DeviceToken = "device-token",
            CachedUserToken = "header.eyJleHAiIjo0MTAyNDQ0ODAwfQ.signature",
            UserTokenExpiresAt = DateTime.UtcNow.AddHours(1),
            DeviceId = "device-id",
            ConnectedAt = DateTime.UtcNow
        };
        db.RemarkableAuths.Add(auth);
        await db.SaveChangesAsync();

        var fs = new FakeFileSystem();
        fs.ExistingFiles.Add("C:\\Books\\book.epub");
        fs.FileContents["C:\\Books\\book.epub"] = [1, 2, 3];

        var client = CreateClient(db, (request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Contains("/doc/v2/files", request.RequestUri!.ToString());
            Assert.NotNull(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            });
        }, fs);

        var local = new LocalBookFile { FullPath = "C:\\Books\\book.epub", TitleFolder = "Title" };
        var sentName = await client.SendFileAsync(local, CancellationToken.None);

        Assert.Equal("Title", sentName);
        Assert.NotNull(auth.LastSentAt);
    }

    [Fact]
    public async Task SendFileAsync_Throws_When_Not_Connected()
    {
        await using var db = CreateDb();
        var client = CreateClient(db, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        var ex = await Assert.ThrowsAsync<RemarkableException>(() => client.SendFileAsync(new LocalBookFile { FullPath = "C:\\Books\\book.epub" }, CancellationToken.None));
        Assert.Contains("not connected", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendFileAsync_Refreshes_User_Token_When_Cache_Is_Expired()
    {
        await using var db = CreateDb();
        db.RemarkableAuths.Add(new RemarkableAuth
        {
            DeviceToken = "device-token",
            CachedUserToken = "expired-token",
            UserTokenExpiresAt = DateTime.UtcNow.AddMinutes(1),
            DeviceId = "device-id",
            ConnectedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var fs = new FakeFileSystem();
        fs.AddFile("C:\\Books\\book.epub", [1, 2, 3]);
        var calls = new List<string>();
        var client = CreateClient(db, (request, _) =>
        {
            calls.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath.EndsWith("/token/json/2/user/new", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("header.eyJleHAiIjo0MTAyNDQ0ODAwfQ.signature")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok")
            });
        }, fs);

        await client.SendFileAsync(new LocalBookFile { FullPath = "C:\\Books\\book.epub", TitleFolder = "Title" }, CancellationToken.None);

        Assert.Contains(calls, path => path.EndsWith("/token/json/2/user/new", StringComparison.Ordinal));
        Assert.Contains(calls, path => path.EndsWith("/doc/v2/files", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendFileAsync_Throws_When_Token_Refresh_Is_Unauthorized()
    {
        await using var db = CreateDb();
        db.RemarkableAuths.Add(new RemarkableAuth
        {
            DeviceToken = "device-token",
            DeviceId = "device-id",
            ConnectedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var fs = new FakeFileSystem();
        fs.AddFile("C:\\Books\\book.epub", [1, 2, 3]);
        var client = CreateClient(db, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("nope")
        }), fs);

        var ex = await Assert.ThrowsAsync<RemarkableException>(() => client.SendFileAsync(new LocalBookFile { FullPath = "C:\\Books\\book.epub", TitleFolder = "Title" }, CancellationToken.None));
        Assert.Contains("Disconnect and re-pair", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendFileAsync_Prefers_Directory_Epub_Without_Conversion()
    {
        await using var db = CreateDb();
        db.RemarkableAuths.Add(new RemarkableAuth
        {
            DeviceToken = "device-token",
            CachedUserToken = "header.eyJleHAiIjo0MTAyNDQ0ODAwfQ.signature",
            UserTokenExpiresAt = DateTime.UtcNow.AddHours(1),
            DeviceId = "device-id",
            ConnectedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var fs = new FakeFileSystem();
        fs.CreateDirectory("C:\\Books\\Folder");
        fs.AddFile("C:\\Books\\Folder\\book.epub", [1, 2, 3]);
        fs.AddFile("C:\\Books\\Folder\\book.mobi", [4, 5, 6]);

        var client = CreateClient(db, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("ok")
        }), fs);

        var sentName = await client.SendFileAsync(new LocalBookFile { FullPath = "C:\\Books\\Folder", TitleFolder = "Title" }, CancellationToken.None);

        Assert.Equal("Title", sentName);
        Assert.True(fs.FileExists("C:\\Books\\Folder\\book.epub"));
    }

    [Fact]
    public async Task SendFileAsync_Throws_When_Folder_Has_No_Ebook_Files()
    {
        await using var db = CreateDb();
        db.RemarkableAuths.Add(new RemarkableAuth
        {
            DeviceToken = "device-token",
            CachedUserToken = "header.eyJleHAiIjo0MTAyNDQ0ODAwfQ.signature",
            UserTokenExpiresAt = DateTime.UtcNow.AddHours(1),
            DeviceId = "device-id",
            ConnectedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var fs = new FakeFileSystem();
        fs.CreateDirectory("C:\\Books\\Folder");
        fs.AddFile("C:\\Books\\Folder\\notes.txt", [1, 2, 3]);

        var client = CreateClient(db, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)), fs);

        var ex = await Assert.ThrowsAsync<RemarkableException>(() => client.SendFileAsync(new LocalBookFile { FullPath = "C:\\Books\\Folder", TitleFolder = "Title" }, CancellationToken.None));
        Assert.Contains("No ebook files found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("A/B:C*D?E\"F<G>H|", "A-B-C-D-E-F-G-H-")]
    [InlineData("  .  ", "Unknown")]
    public void BuildDisplayName_Sanitizes_Name_Parts(string input, string expectedAuthor)
    {
        var name = RemarkableClient.BuildDisplayName(input, "Series", "1", "Title");
        Assert.StartsWith(expectedAuthor, name);
    }

    private static LibraryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<LibraryDbContext>()
            .UseInMemoryDatabase($"remarkable-tests-{Guid.NewGuid():N}")
            .Options;
        return new LibraryDbContext(options);
    }

    private static RemarkableClient CreateClient(
        LibraryDbContext db,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
        IFileSystem? fs = null)
    {
        var factory = new SimpleHttpClientFactory(new HttpClient(new TestHttpMessageHandler(handler)));
        var opts = Options.Create(new RemarkableOptions());
        var converter = new CalibreConverter(Options.Create(new CalibreOptions()), fs ?? new FakeFileSystem(), new FakeProcessRunner(), NullLogger<CalibreConverter>.Instance);
        return new RemarkableClient(db, factory, opts, converter, fs ?? new FakeFileSystem(), NullLogger<RemarkableClient>.Instance);
    }

    private sealed class SimpleHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(System.Diagnostics.ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken ct)
            => Task.FromResult(new ProcessRunResult(0, "", ""));
    }
}

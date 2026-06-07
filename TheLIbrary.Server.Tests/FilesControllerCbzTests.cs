using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

[Collection("Integration")]
public class FilesControllerCbzTests
{
    [Fact]
    public async Task CbzPages_Lists_Only_Image_Entries()
    {
        // Real file on disk inside a library root (the endpoint reads it directly).
        var dir = Path.Combine(Path.GetTempPath(), "thelib-cbz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var cbzPath = Path.Combine(dir, "comic.cbz");
        await File.WriteAllBytesAsync(cbzPath, TestDocs.Cbz(3)); // 3 jpgs + 1 ComicInfo.xml

        try
        {
            using var factory = new LibraryApiFactory();
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
                db.LibraryLocations.Add(new LibraryLocation { Id = 1, Path = dir, Enabled = true });
                db.Authors.Add(new Author { Id = 1, Name = "A" });
                db.Books.Add(new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL1W", Title = "C", NormalizedTitle = "c" });
                db.LocalBookFiles.Add(new LocalBookFile { Id = 5, BookId = 10, AuthorId = 1, FullPath = cbzPath });
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var pages = await client.GetFromJsonAsync<List<JsonElement>>("/api/files/5/cbz-pages");

            Assert.NotNull(pages);
            Assert.Equal(3, pages!.Count); // ComicInfo.xml excluded
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}

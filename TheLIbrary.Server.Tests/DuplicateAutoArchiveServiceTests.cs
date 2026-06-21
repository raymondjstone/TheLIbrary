using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.IO;
using TheLibrary.Server.Services.Scheduling;
using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

// Verifies the scheduled duplicate auto-archive keeps the best copy of a
// multi-file book and moves the rest to the archive (sources removed), against
// real temp files so the move + source-removal path actually runs.
public sealed class DuplicateAutoArchiveServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tl-dupauto-" + Guid.NewGuid().ToString("N"));
    private readonly string _authorDir;

    public DuplicateAutoArchiveServiceTests()
    {
        _authorDir = Path.Combine(_root, "Auth");
        Directory.CreateDirectory(_authorDir);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task Keeps_Best_Copy_And_Archives_The_Rest()
    {
        var dbName = "dupauto-" + Guid.NewGuid().ToString("N");
        var epub = Path.Combine(_authorDir, "book.epub");
        var pdf = Path.Combine(_authorDir, "book.pdf");
        await File.WriteAllTextAsync(epub, "x");
        await File.WriteAllTextAsync(pdf, "x");

        await using (var seed = new LibraryDbContext(new DbContextOptionsBuilder<LibraryDbContext>().UseInMemoryDatabase(dbName).Options))
        {
            seed.LibraryLocations.Add(new LibraryLocation { Id = 1, Label = "L", Path = _root, Enabled = true, IsPrimary = true, CreatedAt = DateTime.UtcNow });
            seed.Authors.Add(new Author { Id = 1, Name = "Auth" });
            seed.Books.Add(new Book { Id = 10, AuthorId = 1, OpenLibraryWorkKey = "OL10W", Title = "Book", NormalizedTitle = "book" });
            seed.LocalBookFiles.AddRange(
                new LocalBookFile { Id = 1, BookId = 10, AuthorId = 1, AuthorFolder = "Auth", FullPath = epub, IntegrityOk = true, ModifiedAt = DateTime.UtcNow },
                new LocalBookFile { Id = 2, BookId = 10, AuthorId = 1, AuthorFolder = "Auth", FullPath = pdf, IntegrityOk = true, ModifiedAt = DateTime.UtcNow });
            await seed.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddDbContext<LibraryDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();
        var sut = new DuplicateAutoArchiveService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new BackgroundTaskCoordinator(), new SystemFileSystem(),
            NullLogger<DuplicateAutoArchiveService>.Instance);

        var result = await sut.RunForTestsAsync(CancellationToken.None);

        Assert.Equal(1, result.BooksProcessed);
        Assert.Equal(1, result.FilesArchived);

        await using var verify = new LibraryDbContext(new DbContextOptionsBuilder<LibraryDbContext>().UseInMemoryDatabase(dbName).Options);
        var keeper = await verify.LocalBookFiles.FindAsync(1);
        var extra = await verify.LocalBookFiles.FindAsync(2);
        // epub keeper stays in place; pdf extra repointed under the archive folder.
        Assert.Equal(epub.Replace('\\', '/'), keeper!.FullPath.Replace('\\', '/'));
        Assert.Contains("__archive", extra!.FullPath.Replace('\\', '/'));
        // Source removed from disk; archived copy exists.
        Assert.False(File.Exists(pdf));
        Assert.True(File.Exists(extra.FullPath));
        Assert.True(File.Exists(epub));

        // The action was recorded to the activity log.
        Assert.True(await verify.ActivityLog.AnyAsync(a => a.Action == "auto-archive"));
    }
}

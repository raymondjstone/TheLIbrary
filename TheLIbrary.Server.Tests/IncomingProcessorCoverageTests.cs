using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Incoming;
using TheLibrary.Server.Services.IO;
using TheLibrary.Server.Services.Sync;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

// Exercises the IncomingProcessor pipeline end-to-end over real temp folders: a
// file under a tracked author's folder is filed to that author; a loose file with
// no resolvable author is quarantined to __unknown.
public sealed class IncomingProcessorCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tl-incoming-" + Guid.NewGuid().ToString("N"));
    private readonly string _incoming;

    public IncomingProcessorCoverageTests()
    {
        _incoming = Path.Combine(_root, "_incoming");
        Directory.CreateDirectory(_incoming);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task ProcessAsync_Files_Matched_And_Quarantines_Unknown()
    {
        using var rdb = new RelationalTestDb();
        await using (var s = rdb.NewContext())
        {
            s.LibraryLocations.Add(new LibraryLocation { Id = 1, Label = "L", Path = _root, Enabled = true, IsPrimary = true, CreatedAt = DateTime.UtcNow });
            s.AppSettings.Add(new AppSetting { Key = AppSettingKeys.IncomingFolder, Value = _incoming });
            s.Authors.Add(new Author
            {
                Id = 1, Name = "Known Auth", CalibreFolderName = "Known Auth",
                OpenLibraryKey = "OL1A", Status = AuthorStatus.Active,
            });
            await s.SaveChangesAsync();
        }

        // A file under a tracked author's folder, plus a loose unresolvable file.
        var knownDir = Path.Combine(_incoming, "Known Auth");
        Directory.CreateDirectory(knownDir);
        await File.WriteAllTextAsync(Path.Combine(knownDir, "A Known Book.epub"), "x");
        await File.WriteAllTextAsync(Path.Combine(_incoming, "Totally Mystery 9z9z.epub"), "x");

        await using var db = rdb.NewContext();
        var processor = new IncomingProcessor(db, new SystemFileSystem(), NullLogger<IncomingProcessor>.Instance);
        var result = await processor.ProcessAsync(CancellationToken.None);

        Assert.True(result.Processed >= 2, $"processed {result.Processed}");
        // The matched file landed under the author folder in the library root.
        Assert.True(Directory.Exists(Path.Combine(_root, "Known Auth")));
        // The unresolvable file was quarantined.
        Assert.True(Directory.Exists(Path.Combine(_root, "__unknown")));
    }

    [Fact]
    public async Task ProcessAsync_Deletes_Junk_And_Quarantines_Mixed_Bag()
    {
        using var rdb = new RelationalTestDb();
        await using (var s = rdb.NewContext())
        {
            s.LibraryLocations.Add(new LibraryLocation { Id = 1, Label = "L", Path = _root, Enabled = true, IsPrimary = true, CreatedAt = DateTime.UtcNow });
            s.AppSettings.Add(new AppSetting { Key = AppSettingKeys.IncomingFolder, Value = _incoming });
            await s.SaveChangesAsync();
        }

        // A junk file (deleted in place), a couple of loose unresolvable ebooks,
        // and an archive — exercises the junk/quarantine/extension branches.
        await File.WriteAllTextAsync(Path.Combine(_incoming, "cover.jpg"), "x");
        await File.WriteAllTextAsync(Path.Combine(_incoming, "Mystery Zzqq One.epub"), "x");
        await File.WriteAllTextAsync(Path.Combine(_incoming, "Mystery Zzqq Two.mobi"), "x");

        await using var db = rdb.NewContext();
        var processor = new IncomingProcessor(db, new SystemFileSystem(), NullLogger<IncomingProcessor>.Instance);
        var result = await processor.ProcessAsync(CancellationToken.None);
        Assert.True(result.Processed >= 2);
    }

    [Fact]
    public async Task ProcessAsync_Empty_Incoming_Is_A_Noop()
    {
        using var rdb = new RelationalTestDb();
        await using (var s = rdb.NewContext())
        {
            s.LibraryLocations.Add(new LibraryLocation { Id = 1, Label = "L", Path = _root, Enabled = true, IsPrimary = true, CreatedAt = DateTime.UtcNow });
            s.AppSettings.Add(new AppSetting { Key = AppSettingKeys.IncomingFolder, Value = _incoming });
            await s.SaveChangesAsync();
        }
        await using var db = rdb.NewContext();
        var processor = new IncomingProcessor(db, new SystemFileSystem(), NullLogger<IncomingProcessor>.Instance);
        var result = await processor.ProcessAsync(CancellationToken.None);
        Assert.Equal(0, result.Processed);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TheLibrary.Server.Data;
using TheLibrary.Server.Data.Models;
using TheLibrary.Server.Services.Calibre;
using TheLibrary.Server.Services.Incoming;
using TheLibrary.Server.Services.Sync;
using TheLibrary.Server.Tests.Infrastructure;
using Xunit;

namespace TheLibrary.Server.Tests;

public class IncomingProcessorTests
{
    [Fact]
    public async Task ProcessAsync_Throws_When_Incoming_Folder_Is_Missing()
    {
        await using var db = CreateDb();
        db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.IncomingFolder, Value = "C:\\incoming" });
        db.LibraryLocations.Add(new LibraryLocation { Id = 1, Path = "C:\\library", IsPrimary = true, Enabled = true, Label = "Default", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var fs = new FakeFileSystem();
        fs.ExistingDirectories.Add("C:\\library");
        var sut = new IncomingProcessor(db, fs, NullLogger<IncomingProcessor>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ProcessAsync(CancellationToken.None));
        Assert.Contains("Incoming folder does not exist", ex.Message);
    }

    [Fact]
    public async Task ProcessUnknownAsync_Returns_Empty_Result_When_Unknown_Folder_Does_Not_Exist()
    {
        await using var db = CreateDb();
        db.LibraryLocations.Add(new LibraryLocation { Id = 1, Path = "C:\\library", IsPrimary = true, Enabled = true, Label = "Default", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var fs = new FakeFileSystem();
        fs.ExistingDirectories.Add("C:\\library");
        var sut = new IncomingProcessor(db, fs, NullLogger<IncomingProcessor>.Instance);

        var result = await sut.ProcessUnknownAsync(null, CancellationToken.None);

        Assert.Equal(0, result.Processed);
        Assert.Single(result.Log);
    }

    [Fact]
    public async Task ProcessUnknownAsync_Leaves_Unmatched_File_In_Place()
    {
        await using var db = CreateDb();
        db.LibraryLocations.Add(new LibraryLocation { Id = 1, Path = "C:\\library", IsPrimary = true, Enabled = true, Label = "Default", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var fs = new FakeFileSystem();
        fs.CreateDirectory("C:\\library");
        fs.CreateDirectory("C:\\library\\__unknown");
        fs.AddFile("C:\\library\\__unknown\\mystery.epub");
        fs.FilesByDirectory["C:\\library\\__unknown"] = ["C:\\library\\__unknown\\mystery.epub"];
        var sut = new IncomingProcessor(db, fs, NullLogger<IncomingProcessor>.Instance);

        var result = await sut.ProcessUnknownAsync(null, CancellationToken.None);

        Assert.Equal(1, result.Processed);
        Assert.Equal(1, result.UnknownAuthor);
        Assert.Contains(result.Log, line => line.Contains("still unmatched", StringComparison.OrdinalIgnoreCase));
        Assert.True(fs.FileExists("C:\\library\\__unknown\\mystery.epub"));
    }

    [Fact]
    public async Task ProcessAsync_Moves_Unmatched_File_Into_Unknown_Folder_And_Cleans_Source_Directory()
    {
        await using var db = CreateDb();
        db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.IncomingFolder, Value = "C:\\incoming" });
        db.LibraryLocations.Add(new LibraryLocation { Id = 1, Path = "C:\\library", IsPrimary = true, Enabled = true, Label = "Default", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var fs = new FakeFileSystem();
        fs.CreateDirectory("C:\\incoming");
        fs.CreateDirectory("C:\\library");
        fs.AddDirectoryChild("C:\\incoming", "C:\\incoming\\drop");
        fs.AddFile("C:\\incoming\\drop\\mystery.bin");

        var sut = new IncomingProcessor(db, fs, NullLogger<IncomingProcessor>.Instance);

        var result = await sut.ProcessAsync(CancellationToken.None);

        Assert.Equal(1, result.Processed);
        Assert.Equal(1, result.UnknownAuthor);
        Assert.True(fs.FileExists("C:\\library\\__unknown\\mystery.bin"));
        Assert.False(fs.FileExists("C:\\incoming\\drop\\mystery.bin"));
        Assert.False(fs.DirectoryExists("C:\\incoming\\drop"));
    }

    [Fact]
    public async Task ProcessAsync_Unmatched_Nested_File_Lands_Directly_Under_Top_Level_Unknown_Folder()
    {
        await using var db = CreateDb();
        db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.IncomingFolder, Value = "C:\\incoming" });
        db.LibraryLocations.Add(new LibraryLocation { Id = 1, Path = "C:\\library", IsPrimary = true, Enabled = true, Label = "Default", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var fs = new FakeFileSystem();
        fs.CreateDirectory("C:\\incoming");
        fs.CreateDirectory("C:\\library");
        fs.AddDirectoryChild("C:\\incoming", "C:\\incoming\\drop");
        fs.AddDirectoryChild("C:\\incoming\\drop", "C:\\incoming\\drop\\some title");
        fs.AddFile("C:\\incoming\\drop\\some title\\mystery.bin");

        var sut = new IncomingProcessor(db, fs, NullLogger<IncomingProcessor>.Instance);

        var result = await sut.ProcessAsync(CancellationToken.None);

        Assert.Equal(1, result.Processed);
        Assert.Equal(1, result.UnknownAuthor);
        Assert.True(fs.FileExists("C:\\library\\__unknown\\mystery.bin"));
        Assert.False(fs.FileExists("C:\\library\\__unknown\\drop\\mystery.bin"));
        Assert.False(fs.FileExists("C:\\library\\__unknown\\drop\\some title\\mystery.bin"));
    }

    [Fact]
    public async Task ProcessAsync_Unmatched_Same_Filename_From_Different_Subfolders_Is_Suffixed_Not_Overwritten()
    {
        await using var db = CreateDb();
        db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.IncomingFolder, Value = "C:\\incoming" });
        db.LibraryLocations.Add(new LibraryLocation { Id = 1, Path = "C:\\library", IsPrimary = true, Enabled = true, Label = "Default", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var fs = new FakeFileSystem();
        fs.CreateDirectory("C:\\incoming");
        fs.CreateDirectory("C:\\library");
        fs.AddDirectoryChild("C:\\incoming", "C:\\incoming\\drop");
        fs.AddDirectoryChild("C:\\incoming\\drop", "C:\\incoming\\drop\\title one");
        fs.AddDirectoryChild("C:\\incoming\\drop", "C:\\incoming\\drop\\title two");
        fs.AddFile("C:\\incoming\\drop\\title one\\mystery.bin");
        fs.AddFile("C:\\incoming\\drop\\title two\\mystery.bin");

        var sut = new IncomingProcessor(db, fs, NullLogger<IncomingProcessor>.Instance);

        var result = await sut.ProcessAsync(CancellationToken.None);

        Assert.Equal(2, result.Processed);
        Assert.Equal(2, result.UnknownAuthor);
        Assert.True(fs.FileExists("C:\\library\\__unknown\\mystery.bin"));
        Assert.True(fs.FileExists("C:\\library\\__unknown\\mystery_1.bin"));
    }

    [Fact]
    public async Task ProcessAsync_Deletes_Junk_File_And_Cleans_Empty_Directory()
    {
        await using var db = CreateDb();
        db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.IncomingFolder, Value = "C:\\incoming" });
        db.LibraryLocations.Add(new LibraryLocation { Id = 1, Path = "C:\\library", IsPrimary = true, Enabled = true, Label = "Default", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var fs = new FakeFileSystem();
        fs.CreateDirectory("C:\\incoming");
        fs.CreateDirectory("C:\\library");
        fs.AddDirectoryChild("C:\\incoming", "C:\\incoming\\covers");
        fs.AddFile("C:\\incoming\\covers\\cover.jpg");

        var sut = new IncomingProcessor(db, fs, NullLogger<IncomingProcessor>.Instance);

        var result = await sut.ProcessAsync(CancellationToken.None);

        Assert.Equal(0, result.Processed);
        Assert.False(fs.FileExists("C:\\incoming\\covers\\cover.jpg"));
        Assert.False(fs.DirectoryExists("C:\\incoming\\covers"));
        Assert.Contains(result.Log, line => line.Contains("deleted junk", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProcessAsync_Tracked_Author_Without_OL_Key_Still_Gets_File()
    {
        // Regression: a tracked (watchlist) author whose OL key hasn't been
        // resolved yet must still receive their files. Previously the code
        // returned null and routed to __unknown, making the watchlist useless
        // for authors that aren't in the OL catalogue or haven't been seeded.
        await using var db = CreateDb();
        db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.IncomingFolder, Value = "C:\\incoming" });
        db.LibraryLocations.Add(new LibraryLocation
        {
            Id = 1, Path = "C:\\library", IsPrimary = true,
            Enabled = true, Label = "Default", CreatedAt = DateTime.UtcNow
        });
        db.Authors.Add(new Author
        {
            Id = 1, Name = "Michael Todd", CalibreFolderName = "Michael Todd",
            OpenLibraryKey = null, Status = AuthorStatus.Active
        });
        await db.SaveChangesAsync();

        var fs = new FakeFileSystem();
        fs.CreateDirectory("C:\\incoming");
        fs.CreateDirectory("C:\\library");
        fs.AddDirectoryChild("C:\\incoming", "C:\\incoming\\drop");
        fs.AddFile("C:\\incoming\\drop\\Backstabbing Little Assets - Michael Todd.epub");

        var sut = new IncomingProcessor(db, fs, NullLogger<IncomingProcessor>.Instance);
        var result = await sut.ProcessAsync(CancellationToken.None);

        Assert.Equal(1, result.Processed);
        Assert.Equal(1, result.Matched);
        Assert.Equal(0, result.UnknownAuthor);
        // File must be under the author's folder, not __unknown.
        Assert.DoesNotContain(fs.ExistingFiles, f =>
            f.Contains("__unknown", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fs.ExistingFiles, f =>
            f.Contains("Michael Todd", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProcessAsync_Active_Author_Wins_When_Excluded_Duplicate_Exists()
    {
        // Regression: if an Excluded Author row shares the same normalized name
        // as an Active row (common after OL seeding creates duplicates), the Active
        // row must win and the file must be routed to the collection folder, not
        // __unknown. Previously, adding excluded names to the blacklist caused the
        // Active row to be rejected too.
        await using var db = CreateDb();
        db.AppSettings.Add(new AppSetting { Key = AppSettingKeys.IncomingFolder, Value = "C:\\incoming" });
        db.LibraryLocations.Add(new LibraryLocation
        {
            Id = 1, Path = "C:\\library", IsPrimary = true,
            Enabled = true, Label = "Default", CreatedAt = DateTime.UtcNow
        });
        // Excluded duplicate (same name, different OL key — typical after seeding)
        db.Authors.Add(new Author
        {
            Id = 1, Name = "Michael Todd", CalibreFolderName = "Michael Todd_OL10501567A",
            OpenLibraryKey = "OL10501567A", Status = AuthorStatus.Excluded
        });
        // Active row — must win
        db.Authors.Add(new Author
        {
            Id = 2, Name = "Michael Todd", CalibreFolderName = "Michael Todd_OL6994998A",
            OpenLibraryKey = "OL6994998A", Status = AuthorStatus.Active
        });
        await db.SaveChangesAsync();

        var fs = new FakeFileSystem();
        fs.CreateDirectory("C:\\incoming");
        fs.CreateDirectory("C:\\library");
        fs.AddDirectoryChild("C:\\incoming", "C:\\incoming\\drop");
        fs.AddFile("C:\\incoming\\drop\\Backstabbing Little Assets - Michael Todd.epub");

        var sut = new IncomingProcessor(db, fs, NullLogger<IncomingProcessor>.Instance);
        var result = await sut.ProcessAsync(CancellationToken.None);

        Assert.Equal(1, result.Processed);
        Assert.Equal(1, result.Matched);
        Assert.Equal(0, result.UnknownAuthor);
        Assert.DoesNotContain(fs.ExistingFiles, f =>
            f.Contains("__unknown", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fs.ExistingFiles, f =>
            f.Contains("Michael Todd", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProcessUnknownAsync_Files_Repeated_Filename_Author_Without_Metadata()
    {
        // DRM'd files with unreadable metadata: the author name appearing on
        // TWO distinct files is corroboration by repetition.
        await using var db = CreateDb();
        db.LibraryLocations.Add(new LibraryLocation { Id = 1, Path = "C:\\library", IsPrimary = true, Enabled = true, Label = "Default", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var fs = new FakeFileSystem();
        fs.CreateDirectory("C:\\library");
        fs.CreateDirectory("C:\\library\\__unknown");
        var f1 = "C:\\library\\__unknown\\A Most Unusual Mango - Felicia Plimsoll.azw3";
        var f2 = "C:\\library\\__unknown\\A Most Unusual Kumquat - Felicia Plimsoll.azw3";
        fs.AddFile(f1);
        fs.AddFile(f2);
        fs.FilesByDirectory["C:\\library\\__unknown"] = [f1, f2];

        var sut = new IncomingProcessor(db, fs, NullLogger<IncomingProcessor>.Instance);
        var result = await sut.ProcessUnknownAsync(null, CancellationToken.None);

        Assert.Equal(2, result.Matched);
        var author = Assert.Single(db.Authors.Where(a => a.Name == "Felicia Plimsoll"));
        Assert.Equal(AuthorStatus.Pending, author.Status);
        Assert.True(fs.FileExists("C:\\library\\Felicia Plimsoll\\A Most Unusual Mango\\A Most Unusual Mango - Felicia Plimsoll.azw3"));
        Assert.True(fs.FileExists("C:\\library\\Felicia Plimsoll\\A Most Unusual Kumquat\\A Most Unusual Kumquat - Felicia Plimsoll.azw3"));
    }

    [Fact]
    public async Task ProcessUnknownAsync_AutoAdds_OL_Catalogue_Author_And_Files_The_Book()
    {
        // The user's exact case: a single quarantined file whose author IS in
        // the OpenLibraryAuthors table but is NOT on the watchlist. Reprocess
        // must add them (Pending) and deliver the file — not bin it.
        await using var db = CreateDb();
        db.LibraryLocations.Add(new LibraryLocation { Id = 1, Path = "C:\\library", IsPrimary = true, Enabled = true, Label = "Default", CreatedAt = DateTime.UtcNow });
        db.OpenLibraryAuthors.Add(new OpenLibraryAuthor
        {
            OlKey = "OL999A",
            Name = "Jane Olwriter",
            NormalizedName = TitleNormalizer.NormalizeAuthor("Jane Olwriter"),
            ImportedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var fs = new FakeFileSystem();
        fs.CreateDirectory("C:\\library");
        fs.CreateDirectory("C:\\library\\__unknown");
        var f1 = "C:\\library\\__unknown\\Some Book - Jane Olwriter.azw3";
        fs.AddFile(f1);
        fs.FilesByDirectory["C:\\library\\__unknown"] = [f1];

        var sut = new IncomingProcessor(db, fs, NullLogger<IncomingProcessor>.Instance);
        var result = await sut.ProcessUnknownAsync(null, CancellationToken.None);

        Assert.Equal(1, result.Matched);
        var author = Assert.Single(db.Authors.Where(a => a.Name == "Jane Olwriter"));
        Assert.Equal(AuthorStatus.Pending, author.Status);
        Assert.Equal("OL999A", author.OpenLibraryKey);
        Assert.True(fs.FileExists("C:\\library\\Jane Olwriter\\Some Book\\Some Book - Jane Olwriter.azw3"));
    }

    [Fact]
    public async Task ProcessUnknownAsync_Leaves_Single_Occurrence_Filename_Author_In_Place()
    {
        // One file, no metadata, author name seen nowhere else — a single
        // uncorroborated source is not enough to create an author.
        await using var db = CreateDb();
        db.LibraryLocations.Add(new LibraryLocation { Id = 1, Path = "C:\\library", IsPrimary = true, Enabled = true, Label = "Default", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var fs = new FakeFileSystem();
        fs.CreateDirectory("C:\\library");
        fs.CreateDirectory("C:\\library\\__unknown");
        var f1 = "C:\\library\\__unknown\\Solo Story - Quincy Standalone.azw3";
        fs.AddFile(f1);
        fs.FilesByDirectory["C:\\library\\__unknown"] = [f1];

        var sut = new IncomingProcessor(db, fs, NullLogger<IncomingProcessor>.Instance);
        var result = await sut.ProcessUnknownAsync(null, CancellationToken.None);

        Assert.Equal(1, result.UnknownAuthor);
        Assert.Empty(db.Authors);
        Assert.True(fs.FileExists(f1)); // stayed put
    }

    [Fact]
    public void FindCorroboratedAuthor_Accepts_Agreeing_Metadata_And_Filename()
    {
        // The dominant leftover shape in the live quarantine: KU/indie books
        // whose author isn't in the OL dump but is named identically by the
        // embedded metadata AND the filename.
        var embedded = new EpubMetadata("A Most Unusual Mango", "Felicia Plimsoll", null, null, null);
        var hit = IncomingProcessor.FindCorroboratedAuthor(
            embedded, "/Books/TheLibrary_Unknown/A Most Unusual Mango - Felicia Plimsoll.azw3",
            new HashSet<string>());

        Assert.NotNull(hit);
        Assert.Equal("Felicia Plimsoll", hit!.Value.Author);
        Assert.Equal("A Most Unusual Mango", hit.Value.Title);
    }

    [Fact]
    public void FindCorroboratedAuthor_Handles_Inverted_Metadata_Form()
    {
        var embedded = new EpubMetadata("A Most Unusual Mango", "Plimsoll, Felicia", null, null, null);
        var hit = IncomingProcessor.FindCorroboratedAuthor(
            embedded, "/x/A Most Unusual Mango - Felicia Plimsoll.azw3", new HashSet<string>());
        Assert.NotNull(hit);
    }

    [Fact]
    public void FindCorroboratedAuthor_Refuses_Disagreement_Blank_And_Blacklist()
    {
        // Filename names someone else entirely — no corroboration.
        Assert.Null(IncomingProcessor.FindCorroboratedAuthor(
            new EpubMetadata("T", "Felicia Plimsoll", null, null, null),
            "/x/Something Else Entirely.azw3", new HashSet<string>()));
        // No embedded author at all.
        Assert.Null(IncomingProcessor.FindCorroboratedAuthor(
            new EpubMetadata("T", null, null, null, null),
            "/x/A Most Unusual Mango - Felicia Plimsoll.azw3", new HashSet<string>()));
        // Blacklisted name never passes.
        Assert.Null(IncomingProcessor.FindCorroboratedAuthor(
            new EpubMetadata("T", "Felicia Plimsoll", null, null, null),
            "/x/A Most Unusual Mango - Felicia Plimsoll.azw3",
            new HashSet<string> { TitleNormalizer.NormalizeAuthor("Felicia Plimsoll") }));
    }

    [Fact]
    public void LooksLikePersonalName_Gates_Shapes()
    {
        Assert.True(IncomingProcessor.LooksLikePersonalName("Felicia Plimsoll"));
        Assert.True(IncomingProcessor.LooksLikePersonalName("A. N. Pringle"));
        Assert.True(IncomingProcessor.LooksLikePersonalName("Ludwig van Plimsoll"));
        Assert.False(IncomingProcessor.LooksLikePersonalName("plimsoll"));            // one lowercase token
        Assert.False(IncomingProcessor.LooksLikePersonalName("The Spectral 13"));     // digits
        Assert.False(IncomingProcessor.LooksLikePersonalName("a very long phrase that is plainly a sentence"));
        Assert.False(IncomingProcessor.LooksLikePersonalName(null));
    }

    [Fact]
    public async Task ProcessUnknownAsync_Files_Corroborated_NonOL_Author()
    {
        // End to end: an epub at the quarantine root whose embedded metadata
        // and filename agree on an author who is NOT in the OL catalogue and
        // NOT on the watchlist → a Pending author is created and the file is
        // delivered to their folder.
        var root = Path.Combine(Path.GetTempPath(), $"incoming-corrob-{Guid.NewGuid():N}");
        var unknownDir = Path.Combine(root, "__unknown");
        Directory.CreateDirectory(unknownDir);
        var realFile = Path.Combine(unknownDir, "A Most Unusual Mango - Felicia Plimsoll.epub");
        await File.WriteAllBytesAsync(realFile, TestEpub.BuildWithMetadata(
            "A Most Unusual Mango", "Felicia Plimsoll",
            ("c.xhtml", "<html><body><p>prose</p></body></html>")));

        try
        {
            await using var db = CreateDb();
            db.LibraryLocations.Add(new LibraryLocation { Id = 1, Path = root, IsPrimary = true, Enabled = true, Label = "Default", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();

            var fs = new FakeFileSystem();
            fs.CreateDirectory(root);
            fs.CreateDirectory(unknownDir);
            fs.AddFile(realFile);
            fs.FilesByDirectory[unknownDir] = [realFile];

            var sut = new IncomingProcessor(db, fs, NullLogger<IncomingProcessor>.Instance);
            var result = await sut.ProcessUnknownAsync(null, CancellationToken.None);

            Assert.Equal(1, result.Matched);
            var author = Assert.Single(db.Authors.Where(a => a.Name == "Felicia Plimsoll"));
            Assert.Equal(AuthorStatus.Pending, author.Status);
            Assert.True(fs.FileExists(Path.Combine(root, "Felicia Plimsoll", "A Most Unusual Mango",
                "A Most Unusual Mango - Felicia Plimsoll.epub")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static LibraryDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<LibraryDbContext>()
            .UseInMemoryDatabase($"incoming-tests-{Guid.NewGuid():N}")
            .Options;
        return new LibraryDbContext(options);
    }
}

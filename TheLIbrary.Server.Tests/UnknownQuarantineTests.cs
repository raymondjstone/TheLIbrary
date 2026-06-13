using TheLibrary.Server.Services.Sync;
using Xunit;

namespace TheLibrary.Server.Tests;

// The quarantine is a FLAT bucket — these lock the shared helper every writer
// (sync MoveToUnknown / self-heal, the to-unknown action, the quarantine-path
// migration) routes through, so a folder can never be created under __unknown.
public class UnknownQuarantineTests
{
    [Fact]
    public void FlattenFolderIntoRoot_Lifts_Nested_Files_To_Root_And_Deletes_Source_Tree()
    {
        var root = Path.Combine(Path.GetTempPath(), $"uq-{Guid.NewGuid():N}");
        var unknownRoot = Path.Combine(root, "__unknown");
        var authorDir = Path.Combine(unknownRoot, "Quigley Fenwick_OL123A");
        var titleDir = Path.Combine(authorDir, "Some Title");
        Directory.CreateDirectory(titleDir);
        File.WriteAllText(Path.Combine(authorDir, "loose.epub"), "a");
        File.WriteAllText(Path.Combine(titleDir, "nested.epub"), "b");

        try
        {
            var rewrites = new Dictionary<string, string>();
            var moved = UnknownQuarantine.FlattenFolderIntoRoot(unknownRoot, authorDir, rewrites);

            Assert.Equal(2, moved);
            Assert.True(File.Exists(Path.Combine(unknownRoot, "loose.epub")));
            Assert.True(File.Exists(Path.Combine(unknownRoot, "nested.epub")));
            Assert.False(Directory.Exists(authorDir));   // the whole author folder tree is gone
            Assert.Equal(2, rewrites.Count);             // every move recorded for the DB pass
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FlattenFolderIntoRoot_Suffixes_On_Collision_Never_Overwrites()
    {
        var root = Path.Combine(Path.GetTempPath(), $"uq-{Guid.NewGuid():N}");
        var unknownRoot = Path.Combine(root, "__unknown");
        var authorDir = Path.Combine(unknownRoot, "Author_OL9A");
        var titleDir = Path.Combine(authorDir, "T");
        Directory.CreateDirectory(titleDir);
        File.WriteAllText(Path.Combine(unknownRoot, "book.epub"), "existing-at-root");
        File.WriteAllText(Path.Combine(titleDir, "book.epub"), "incoming");

        try
        {
            UnknownQuarantine.FlattenFolderIntoRoot(unknownRoot, authorDir);

            Assert.Equal("existing-at-root", File.ReadAllText(Path.Combine(unknownRoot, "book.epub")));
            Assert.True(File.Exists(Path.Combine(unknownRoot, "book_2.epub")));
            Assert.Equal("incoming", File.ReadAllText(Path.Combine(unknownRoot, "book_2.epub")));
            Assert.False(Directory.Exists(authorDir));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}

namespace TheLibrary.Server.Services.Sync;

// The __unknown quarantine is a FLAT bucket: loose files only, NEVER author or
// title subfolders. Every path that relocates content into quarantine routes
// through here so a folder structure can never be (re)created under it — the
// reprocess-unknown job re-derives each file's author from its name/metadata,
// so folder grouping inside quarantine carries no value and only clutters it.
public static class UnknownQuarantine
{
    // Moves every file beneath sourceDir (recursively) flat into unknownRoot,
    // resolving name collisions with _N suffixing, then deletes the drained
    // source tree. Returns the number of files moved. When `rewrites` is given,
    // each (oldFullPath → newFlatPath) move is recorded so the caller can update
    // matching DB rows.
    public static int FlattenFolderIntoRoot(
        string unknownRoot, string sourceDir, IDictionary<string, string>? rewrites = null)
    {
        if (!Directory.Exists(sourceDir)) return 0;
        Directory.CreateDirectory(unknownRoot);

        var moved = 0;
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var dest = UntrackedAuthorAssigner.UniqueFilePath(
                Path.Combine(unknownRoot, Path.GetFileName(file)));
            File.Move(file, dest);
            if (rewrites is not null) rewrites[file] = dest;
            moved++;
        }

        try { Directory.Delete(sourceDir, recursive: true); } catch { /* best effort */ }
        return moved;
    }

    // Moves a single file flat into unknownRoot (collision-suffixed) and returns
    // its final path.
    public static string MoveFileFlat(string unknownRoot, string sourceFile)
    {
        Directory.CreateDirectory(unknownRoot);
        var dest = UntrackedAuthorAssigner.UniqueFilePath(
            Path.Combine(unknownRoot, Path.GetFileName(sourceFile)));
        File.Move(sourceFile, dest);
        return dest;
    }
}

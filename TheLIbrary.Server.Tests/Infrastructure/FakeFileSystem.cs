using TheLibrary.Server.Services.IO;

namespace TheLibrary.Server.Tests.Infrastructure;

internal sealed class FakeFileSystem : IFileSystem
{
    public HashSet<string> ExistingDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ExistingFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> FilesByDirectory { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> DirectoriesByDirectory { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, byte[]> FileContents { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool DirectoryExists(string path) => ExistingDirectories.Contains(path);
    public bool FileExists(string path) => ExistingFiles.Contains(path);
    public void CreateDirectory(string path)
    {
        ExistingDirectories.Add(path);
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            ExistingDirectories.Add(parent);
            AddUnique(DirectoriesByDirectory, parent, path);
        }
    }
    public void DeleteFile(string path)
    {
        ExistingFiles.Remove(path);
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent) && FilesByDirectory.TryGetValue(parent, out var files))
            files.RemoveAll(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
    }
    public void DeleteDirectory(string path)
    {
        ExistingDirectories.Remove(path);
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent) && DirectoriesByDirectory.TryGetValue(parent, out var dirs))
            dirs.RemoveAll(d => string.Equals(d, path, StringComparison.OrdinalIgnoreCase));
    }
    public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
    {
        if (!ExistingFiles.Remove(sourcePath)) throw new FileNotFoundException(sourcePath);
        if (!overwrite && ExistingFiles.Contains(destinationPath)) throw new IOException("exists");
        DeleteFile(destinationPath);
        ExistingFiles.Add(destinationPath);
        if (FileContents.Remove(sourcePath, out var bytes)) FileContents[destinationPath] = bytes;
        var sourceParent = Path.GetDirectoryName(sourcePath);
        if (!string.IsNullOrWhiteSpace(sourceParent) && FilesByDirectory.TryGetValue(sourceParent, out var sourceFiles))
            sourceFiles.RemoveAll(f => string.Equals(f, sourcePath, StringComparison.OrdinalIgnoreCase));
        var destParent = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destParent))
        {
            CreateDirectory(destParent);
            AddUnique(FilesByDirectory, destParent, destinationPath);
        }
    }
    public void MoveDirectory(string sourcePath, string destinationPath)
    {
        if (!ExistingDirectories.Remove(sourcePath)) throw new DirectoryNotFoundException(sourcePath);
        CreateDirectory(destinationPath);
        DeleteDirectory(sourcePath);
    }
    public IEnumerable<string> EnumerateFiles(string path) => FilesByDirectory.TryGetValue(path, out var files) ? files : Enumerable.Empty<string>();
    public IEnumerable<string> EnumerateFiles(string path, string searchPattern, EnumerationOptions options) => EnumerateFiles(path);
    public IEnumerable<string> EnumerateDirectories(string path) => DirectoriesByDirectory.TryGetValue(path, out var dirs) ? dirs : Enumerable.Empty<string>();
    public IEnumerable<string> EnumerateDirectories(string path, string searchPattern, EnumerationOptions options) => EnumerateDirectories(path);
    public IEnumerable<string> EnumerateFileSystemEntries(string path) => EnumerateDirectories(path).Concat(EnumerateFiles(path));
    public Stream OpenRead(string path)
    {
        if (!FileContents.TryGetValue(path, out var bytes)) throw new FileNotFoundException(path);
        return new MemoryStream(bytes, writable: false);
    }

    public void AddFile(string path, byte[]? contents = null)
    {
        ExistingFiles.Add(path);
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            CreateDirectory(parent);
            AddUnique(FilesByDirectory, parent, path);
        }
        if (contents is not null) FileContents[path] = contents;
    }

    public void AddDirectoryChild(string parent, string child)
    {
        CreateDirectory(parent);
        CreateDirectory(child);
        AddUnique(DirectoriesByDirectory, parent, child);
    }

    private static void AddUnique(Dictionary<string, List<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<string>();
        if (!list.Contains(value, StringComparer.OrdinalIgnoreCase)) list.Add(value);
    }
}

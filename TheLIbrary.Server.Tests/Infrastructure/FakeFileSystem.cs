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
    public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult(DirectoryExists(path));
    public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult(FileExists(path));
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
    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        CreateDirectory(path);
        return Task.CompletedTask;
    }
    public void DeleteFile(string path)
    {
        ExistingFiles.Remove(path);
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent) && FilesByDirectory.TryGetValue(parent, out var files))
            files.RemoveAll(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
    }
    public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        DeleteFile(path);
        return Task.CompletedTask;
    }
    public void DeleteDirectory(string path)
    {
        ExistingDirectories.Remove(path);
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent) && DirectoriesByDirectory.TryGetValue(parent, out var dirs))
            dirs.RemoveAll(d => string.Equals(d, path, StringComparison.OrdinalIgnoreCase));
    }
    public void DeleteDirectory(string path, bool recursive) => DeleteDirectory(path);
    public Task DeleteDirectoryAsync(string path, bool recursive, CancellationToken cancellationToken = default)
    {
        DeleteDirectory(path, recursive);
        return Task.CompletedTask;
    }
    // Opt-in: source paths whose move copies to the destination but LEAVES the
    // source behind — simulating a cross-mount File.Move (copy+delete) whose source
    // delete silently fails on a NAS share. Default empty → normal move semantics.
    public HashSet<string> MoveLeavesSource { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
    {
        var leaveSource = MoveLeavesSource.Contains(sourcePath);
        if (!ExistingFiles.Remove(sourcePath)) throw new FileNotFoundException(sourcePath);
        if (!overwrite && ExistingFiles.Contains(destinationPath)) throw new IOException("exists");
        DeleteFile(destinationPath);
        ExistingFiles.Add(destinationPath);
        if (leaveSource) ExistingFiles.Add(sourcePath); // cross-mount: source not actually removed
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
    public Task MoveFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default)
    {
        MoveFile(sourcePath, destinationPath, overwrite);
        return Task.CompletedTask;
    }
    public void MoveDirectory(string sourcePath, string destinationPath)
    {
        if (!ExistingDirectories.Remove(sourcePath)) throw new DirectoryNotFoundException(sourcePath);
        CreateDirectory(destinationPath);
        DeleteDirectory(sourcePath);
    }
    public Task MoveDirectoryAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        MoveDirectory(sourcePath, destinationPath);
        return Task.CompletedTask;
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

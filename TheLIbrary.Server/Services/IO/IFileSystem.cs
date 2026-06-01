namespace TheLibrary.Server.Services.IO;

// Shared path helpers. Centralised so every mover uses the same notion of
// "these two paths are the same place" when deciding whether a move is a no-op.
public static class FsPath
{
    // True when source and destination refer to the same filesystem location, so
    // a move would be a self-move (which File.Move/Directory.Move reject with an
    // IOException). Comparison is case-insensitive — the library lives on a
    // case-insensitive SMB share — and normalises separators / "." / ".."
    // segments so differently-written-but-equivalent paths still match. A
    // genuine case-only rename ("book.epub" → "Book.epub") therefore counts as
    // the same location and is skipped, matching the existing organiser guards.
    public static bool SameLocation(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
            return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // GetFullPath can throw on malformed paths — fall back to the plain
            // string comparison already done above (i.e. not the same).
            return false;
        }
    }
}

public interface IFileSystem
{
    bool DirectoryExists(string path);
    bool FileExists(string path);
    Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken = default);
    Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default);
    void CreateDirectory(string path);
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);
    void DeleteFile(string path);
    Task DeleteFileAsync(string path, CancellationToken cancellationToken = default);
    void DeleteDirectory(string path);
    void DeleteDirectory(string path, bool recursive);
    Task DeleteDirectoryAsync(string path, bool recursive, CancellationToken cancellationToken = default);
    void MoveFile(string sourcePath, string destinationPath, bool overwrite);
    Task MoveFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default);
    void MoveDirectory(string sourcePath, string destinationPath);
    Task MoveDirectoryAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);
    IEnumerable<string> EnumerateFiles(string path);
    IEnumerable<string> EnumerateFiles(string path, string searchPattern, EnumerationOptions options);
    IEnumerable<string> EnumerateDirectories(string path);
    IEnumerable<string> EnumerateDirectories(string path, string searchPattern, EnumerationOptions options);
    IEnumerable<string> EnumerateFileSystemEntries(string path);
    Stream OpenRead(string path);
}

public sealed class SystemFileSystem : IFileSystem
{
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public bool FileExists(string path) => File.Exists(path);
    public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run(() => Directory.Exists(path), cancellationToken);
    public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run(() => File.Exists(path), cancellationToken);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run(() => Directory.CreateDirectory(path), cancellationToken);
    public void DeleteFile(string path) => File.Delete(path);
    public Task DeleteFileAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run(() => File.Delete(path), cancellationToken);
    public void DeleteDirectory(string path) => Directory.Delete(path);
    public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);
    public Task DeleteDirectoryAsync(string path, bool recursive, CancellationToken cancellationToken = default)
        => Task.Run(() => Directory.Delete(path, recursive), cancellationToken);
    // All four movers no-op when source and destination are the same location,
    // so callers never have to special-case a move-onto-self (which the
    // underlying File.Move/Directory.Move would throw on).
    public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
    {
        if (FsPath.SameLocation(sourcePath, destinationPath)) return;
        File.Move(sourcePath, destinationPath, overwrite);
    }
    public Task MoveFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            if (FsPath.SameLocation(sourcePath, destinationPath)) return;
            File.Move(sourcePath, destinationPath, overwrite);
        }, cancellationToken);
    public void MoveDirectory(string sourcePath, string destinationPath)
    {
        if (FsPath.SameLocation(sourcePath, destinationPath)) return;
        Directory.Move(sourcePath, destinationPath);
    }
    public Task MoveDirectoryAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            if (FsPath.SameLocation(sourcePath, destinationPath)) return;
            Directory.Move(sourcePath, destinationPath);
        }, cancellationToken);
    public IEnumerable<string> EnumerateFiles(string path) => Directory.EnumerateFiles(path);
    public IEnumerable<string> EnumerateFiles(string path, string searchPattern, EnumerationOptions options) => Directory.EnumerateFiles(path, searchPattern, options);
    public IEnumerable<string> EnumerateDirectories(string path) => Directory.EnumerateDirectories(path);
    public IEnumerable<string> EnumerateDirectories(string path, string searchPattern, EnumerationOptions options) => Directory.EnumerateDirectories(path, searchPattern, options);
    public IEnumerable<string> EnumerateFileSystemEntries(string path) => Directory.EnumerateFileSystemEntries(path);
    public Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
}

namespace TheLibrary.Server.Services.IO;

// Move helpers that GUARANTEE the source is gone afterward.
//
// On the CIFS/NFS mounts this library lives on, File.Move / Directory.Move is a
// copy followed by an unlink, and that unlink can be deferred or silently fail —
// leaving the original behind. The next library scan then re-imports the orphan
// as a fresh row and the book reappears as a duplicate "with no new files added"
// (the recurring quarantine/duplicate resurrection bug). After the move, verify
// the destination exists and force-remove any lingering source. The cleanup is
// best-effort: it never throws, so a move that genuinely succeeded isn't reported
// as a failure. The move itself still throws on real errors for the caller.
public static class SafeMove
{
    public static void File(string source, string destination, bool overwrite = false)
    {
        System.IO.File.Move(source, destination, overwrite);
        if (System.IO.File.Exists(source) && System.IO.File.Exists(destination))
        {
            try { System.IO.File.Delete(source); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static void Directory(string source, string destination)
    {
        System.IO.Directory.Move(source, destination);
        if (System.IO.Directory.Exists(source) && System.IO.Directory.Exists(destination))
        {
            try { System.IO.Directory.Delete(source, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

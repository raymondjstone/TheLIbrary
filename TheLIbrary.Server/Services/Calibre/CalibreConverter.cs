using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace TheLibrary.Server.Services.Calibre;

// Shells out to Calibre's ebook-convert CLI to turn any supported format
// into EPUB. Used by the reMarkable send path when the source isn't already
// one of reMarkable's two native formats (EPUB / PDF).
public sealed class CalibreConverter
{
    // Conservative upper bound — Calibre conversions are usually well under
    // a minute, but large comics or dense PDFs can take longer.
    private static readonly TimeSpan ConversionTimeout = TimeSpan.FromMinutes(10);

    private readonly CalibreOptions _opts;
    private readonly ILogger<CalibreConverter> _log;

    public CalibreConverter(IOptions<CalibreOptions> opts, ILogger<CalibreConverter> log)
    {
        _opts = opts.Value;
        _log = log;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_opts.EbookConvert);

    // Converts `sourcePath` to EPUB in a new temp file and returns the path
    // to the caller. Caller owns the returned file and must delete it after
    // use (even if the upload fails). Throws CalibreConversionException on
    // any failure (binary missing, non-zero exit code, timeout, …).
    public async Task<string> ConvertToEpubAsync(string sourcePath, CancellationToken ct)
    {
        if (!System.IO.File.Exists(sourcePath))
            throw new CalibreConversionException($"Source file not found: {sourcePath}");
        if (string.IsNullOrWhiteSpace(_opts.EbookConvert))
            throw new CalibreConversionException(
                "ebook-convert path is not configured. Set Calibre:EbookConvert in appsettings.json.");

        var tempDir = Path.Combine(Path.GetTempPath(), "thelibrary-rm");
        Directory.CreateDirectory(tempDir);
        var outPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}.epub");

        var psi = new ProcessStartInfo
        {
            FileName = _opts.EbookConvert,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // ArgumentList avoids shell-quoting issues with spaces / special chars.
        psi.ArgumentList.Add(sourcePath);
        psi.ArgumentList.Add(outPath);

        using var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.Start())
                throw new CalibreConversionException("Failed to start ebook-convert.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new CalibreConversionException(
                $"Could not run ebook-convert ({ex.Message}). " +
                "Install Calibre and set Calibre:EbookConvert in appsettings.json to the ebook-convert executable.", ex);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ConversionTimeout);

        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            TryDelete(outPath);
            if (ct.IsCancellationRequested) throw;
            throw new CalibreConversionException(
                $"ebook-convert exceeded the {ConversionTimeout.TotalMinutes:0}-minute timeout on {Path.GetFileName(sourcePath)}.");
        }

        if (proc.ExitCode != 0)
        {
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            TryDelete(outPath);
            _log.LogWarning("ebook-convert failed: exit {Code} stderr {Stderr}", proc.ExitCode, stderr);
            throw new CalibreConversionException(
                $"ebook-convert exited {proc.ExitCode}. {Trim(stderr)}");
        }

        if (!System.IO.File.Exists(outPath))
            throw new CalibreConversionException("ebook-convert reported success but produced no output file.");

        return outPath;
    }

    private static void TryKill(Process p) { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { } }
    private static void TryDelete(string path) { try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { } }

    private static string Trim(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim();
        return s.Length > 400 ? s[..400] + "…" : s;
    }
}

public sealed class CalibreConversionException : Exception
{
    public CalibreConversionException(string message) : base(message) { }
    public CalibreConversionException(string message, Exception inner) : base(message, inner) { }
}

public sealed class CalibreOptions
{
    public string? Root { get; set; }

    // Path (or bare name, if on PATH) of Calibre's ebook-convert CLI.
    // Leave empty to disable on-the-fly format conversion.
    public string? EbookConvert { get; set; }
}

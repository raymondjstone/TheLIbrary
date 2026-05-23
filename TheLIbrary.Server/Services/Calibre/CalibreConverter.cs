using System.Diagnostics;
using Microsoft.Extensions.Options;
using TheLibrary.Server.Services.IO;

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
    private readonly IFileSystem _fs;
    private readonly IProcessRunner _runner;
    private readonly ILogger<CalibreConverter> _log;

    public CalibreConverter(
        IOptions<CalibreOptions> opts,
        IFileSystem fs,
        IProcessRunner runner,
        ILogger<CalibreConverter> log)
    {
        _opts = opts.Value;
        _fs = fs;
        _runner = runner;
        _log = log;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_opts.EbookConvert);

    // Converts `sourcePath` to EPUB in a new temp file and returns the path
    // to the caller. Caller owns the returned file and must delete it after
    // use (even if the upload fails). Throws CalibreConversionException on
    // any failure (binary missing, non-zero exit code, timeout, …).
    public async Task<string> ConvertToEpubAsync(string sourcePath, CancellationToken ct)
    {
        if (!_fs.FileExists(sourcePath))
            throw new CalibreConversionException($"Source file not found: {sourcePath}");
        if (string.IsNullOrWhiteSpace(_opts.EbookConvert))
            throw new CalibreConversionException(
                "ebook-convert path is not configured. Set Calibre:EbookConvert in appsettings.json.");

        var tempDir = Path.Combine(Path.GetTempPath(), "thelibrary-rm");
        _fs.CreateDirectory(tempDir);
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

        try
        {
            var result = await _runner.RunAsync(psi, ConversionTimeout, ct);

            if (result.ExitCode != 0)
            {
                TryDelete(outPath);
                _log.LogWarning("ebook-convert failed: exit {Code} stderr {Stderr}", result.ExitCode, result.StandardError);
                throw new CalibreConversionException(
                    $"ebook-convert exited {result.ExitCode}. {Trim(result.StandardError)}");
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new CalibreConversionException(
                $"Could not run ebook-convert ({ex.Message}). " +
                "Install Calibre and set Calibre:EbookConvert in appsettings.json to the ebook-convert executable.", ex);
        }
        catch (ProcessRunTimeoutException)
        {
            TryDelete(outPath);
            throw new CalibreConversionException(
                $"ebook-convert exceeded the {ConversionTimeout.TotalMinutes:0}-minute timeout on {Path.GetFileName(sourcePath)}.");
        }

        if (!_fs.FileExists(outPath))
            throw new CalibreConversionException("ebook-convert reported success but produced no output file.");

        return outPath;
    }

    private void TryDelete(string path) { try { if (_fs.FileExists(path)) _fs.DeleteFile(path); } catch { } }

    private static string Trim(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim();
        return s.Length > 400 ? s[..400] + "…" : s;
    }
}

public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken ct);
}

public sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError);

public sealed class ProcessRunTimeoutException : Exception
{
    public ProcessRunTimeoutException() { }
}

public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken ct)
    {
        using var proc = new Process { StartInfo = startInfo };
        if (!proc.Start())
            throw new InvalidOperationException("Failed to start ebook-convert.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            throw new ProcessRunTimeoutException();
        }

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        return new ProcessRunResult(proc.ExitCode, stdout, stderr);
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

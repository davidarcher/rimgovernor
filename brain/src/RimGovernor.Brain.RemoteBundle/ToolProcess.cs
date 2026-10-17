using System.Diagnostics;
using System.Text;

namespace RimGovernor.Brain.RemoteBundle;

/// <summary>The pinned executables bundle operations run; explicit paths from the trusted runner's tool installation.</summary>
/// <param name="SevenZip">The 7-Zip executable.</param>
/// <param name="Age">The age executable.</param>
public sealed record Tools(string SevenZip, string Age);

/// <summary>The captured result of a finished child process.</summary>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="Stdout">The standard output.</param>
/// <param name="Stderr">The standard error.</param>
internal sealed record ProcessResult(int ExitCode, string Stdout, string Stderr)
{
    /// <summary>Standard output followed by standard error.</summary>
    public string Combined => Stdout + Stderr;
}

/// <summary>Runs child programs for the bundle operations; cancelling the token kills the whole child tree.</summary>
internal static class ToolProcess
{
    /// <summary>
    /// Runs a tool that must succeed, with a scrubbed environment (no token, secret, password or private key), and
    /// throws with its output when it fails.
    /// </summary>
    public static async Task RunPublicAsync(string directory, string tool, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await CaptureAsync(directory, tool, arguments, scrub: true, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new BundleException($"{Path.GetFileName(tool)} failed: exit status {result.ExitCode}: {result.Combined}");
        }
    }

    /// <summary>Runs <paramref name="tool"/> and captures its output. A scrubbed environment keeps credentials from the child.</summary>
    public static async Task<ProcessResult> CaptureAsync(string directory, string tool, IReadOnlyList<string> arguments, bool scrub, CancellationToken cancellationToken)
    {
        using var process = Start(directory, tool, arguments, scrub, redirectOutput: true);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    /// <summary>Runs <paramref name="tool"/> with its output streamed into <paramref name="log"/>; returns the exit code.</summary>
    public static async Task<int> StreamAsync(string directory, string tool, IReadOnlyList<string> arguments, bool scrub, TextWriter log, CancellationToken cancellationToken)
    {
        using var process = Start(directory, tool, arguments, scrub, redirectOutput: true);
        var gate = new object();
        async Task Pump(StreamReader reader)
        {
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                lock (gate)
                {
                    log.WriteLine(line);
                }
            }
        }

        var pumps = Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError));
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await pumps.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        return process.ExitCode;
    }

    /// <summary>
    /// Runs <paramref name="tool"/> inheriting the full environment (the GitHub CLI reads its token from it) and copies
    /// its standard output into <paramref name="destination"/>; a non-zero exit or a failed copy throws.
    /// </summary>
    public static async Task PipeOutputAsync(string tool, IReadOnlyList<string> arguments, Stream destination, string failure, CancellationToken cancellationToken)
    {
        using var process = Start("", tool, arguments, scrub: false, redirectOutput: true);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            Kill(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new BundleException($"{failure}: exit status {process.ExitCode}: {await stderr.ConfigureAwait(false)}");
        }
    }

    private static Process Start(string directory, string tool, IReadOnlyList<string> arguments, bool scrub, bool redirectOutput)
    {
        var start = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (directory.Length > 0)
        {
            start.WorkingDirectory = directory;
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (scrub)
        {
            BundleFiles.ScrubEnvironment(start);
        }

        try
        {
            return Process.Start(start) ?? throw new BundleException($"start {tool}: no process");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new BundleException($"start {tool}: {e.Message}", e);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
            return;
        }
    }
}

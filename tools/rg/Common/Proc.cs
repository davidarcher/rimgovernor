using System.Diagnostics;
using System.Text;

namespace RimGovernor.Tools.Common;

/// <summary>Child processes: captured, or streamed with a heartbeat (Go: <c>os/exec</c> plus <c>internal/childproc</c>).</summary>
internal static class Proc
{
    /// <summary>How often a running command reports that it is alive.</summary>
    public static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(30);

    public sealed record Result(int ExitCode, string Stdout, string Stderr);

    /// <summary>Runs to completion with stdout and stderr captured. Never throws on a non-zero exit.</summary>
    public static async Task<Result> CaptureAsync(string dir, string file, IEnumerable<string> args, CancellationToken ct)
    {
        using var process = Start(dir, file, args, redirectOut: true, redirectErr: true);
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await WaitAsync(process, ct).ConfigureAwait(false);
        return new Result(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    /// <summary>Runs to completion and returns stdout untrimmed; a non-zero exit throws with the stderr.</summary>
    public static async Task<string> OutputAsync(string dir, string file, IEnumerable<string> args, CancellationToken ct)
    {
        var argList = args as IReadOnlyCollection<string> ?? args.ToArray();
        var result = await CaptureAsync(dir, file, argList, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new ToolException($"{file} {string.Join(' ', argList)}: exit code {result.ExitCode}: {result.Stderr.Trim()}");
        }
        return result.Stdout;
    }

    /// <summary>
    /// Streams a command so failures are visible, printing a heartbeat to stderr while it is quiet so a slow command
    /// reads as running, not stuck. With <paramref name="stdout"/> the child's stdout is sent there line by line;
    /// otherwise it goes straight to this process's stdout.
    /// </summary>
    public static async Task RunAsync(string dir, string file, IReadOnlyList<string> args, CancellationToken ct, Action<string>? stdout = null)
    {
        using var process = Start(dir, file, args, redirectOut: stdout is not null, redirectErr: false);
        var label = file;
        if (args.Count > 0)
        {
            label += " " + args[0];
            if (args[0] == "tool" && args.Count > 1)
            {
                label += " " + args[1];
            }
        }
        var started = Stopwatch.StartNew();
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = HeartbeatAsync(label, started, heartbeatStop.Token);
        try
        {
            Task pump = Task.CompletedTask;
            if (stdout is not null)
            {
                pump = Task.Run(
                    async () =>
                    {
                        string? line;
                        while ((line = await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                        {
                            stdout(line);
                        }
                    },
                    ct);
            }
            await WaitAsync(process, ct).ConfigureAwait(false);
            await pump.ConfigureAwait(false);
        }
        finally
        {
            await heartbeatStop.CancelAsync().ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
        }
        if (process.ExitCode != 0)
        {
            throw new ToolException($"{file} {string.Join(' ', args)}: exit code {process.ExitCode}");
        }
    }

    private static async Task HeartbeatAsync(string label, Stopwatch started, CancellationToken token)
    {
        using var timer = new PeriodicTimer(HeartbeatEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                await Console.Error.WriteLineAsync($"  still running: {label} ({Durations.Format(started.Elapsed)})").ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The command finished or the tool is stopping.
        }
    }

    private static Process Start(string dir, string file, IEnumerable<string> args, bool redirectOut, bool redirectErr)
    {
        var info = new ProcessStartInfo(file)
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            RedirectStandardOutput = redirectOut,
            RedirectStandardError = redirectErr,
            // A captured console child never opens a terminal window of its own (Go: childproc.HideConsole).
            CreateNoWindow = redirectOut || redirectErr,
        };
        if (redirectOut)
        {
            info.StandardOutputEncoding = Encoding.UTF8;
        }
        if (redirectErr)
        {
            info.StandardErrorEncoding = Encoding.UTF8;
        }
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        try
        {
            return Process.Start(info) ?? throw new ToolException($"{file}: did not start");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new ToolException($"{file}: {e.Message} (is it on PATH?)", e);
        }
    }

    private static async Task WaitAsync(Process process, CancellationToken ct)
    {
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
            throw;
        }
    }
}

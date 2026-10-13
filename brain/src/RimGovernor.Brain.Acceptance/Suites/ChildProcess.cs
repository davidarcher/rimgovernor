using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// How a command runs "this executable" again: the suite and the sweep run each case as a child
/// <c>rg acceptance run</c>, the way the Go suite ran child <c>acceptance</c> processes. The path comes from
/// <see cref="Environment.ProcessPath"/>; when the host is the <c>dotnet</c> launcher (a framework-dependent run) the entry
/// assembly follows it as the first argument.
/// </summary>
public static class SelfInvocation
{
    /// <summary>Replaces the resolved invocation (a test or a host that launches the tool another way); null restores the default.</summary>
    public static (string Executable, IReadOnlyList<string> LeadingArgs)? Override { get; set; }

    /// <summary>The executable and the arguments that precede the <c>acceptance ...</c> arguments.</summary>
    public static (string Executable, IReadOnlyList<string> LeadingArgs) Resolve()
    {
        if (Override is { } fixedInvocation)
        {
            return fixedInvocation;
        }

        var path = Environment.ProcessPath
            ?? throw new AcceptanceException("own executable: the process path is unavailable");
        if (string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var entry = Environment.GetCommandLineArgs() is { Length: > 0 } argv ? argv[0] : "";
            if (entry.Length > 0)
            {
                return (path, [entry]);
            }
        }

        return (path, []);
    }

    /// <summary>The full argv (after the executable) of a child running <c>acceptance <paramref name="args"/></c>.</summary>
    public static IReadOnlyList<string> AcceptanceArgv(IEnumerable<string> args)
    {
        var (_, leading) = Resolve();
        return [.. leading, "acceptance", .. args];
    }
}

/// <summary>Runs one child process with its output captured to a log file and its tree killed when the token ends.</summary>
internal static class ChildProcess
{
    /// <summary>The outcome of a child that started: its exit code.</summary>
    internal readonly record struct Exit(int Code);

    /// <summary>How long output already written by an exited child is given to reach the log before the wait for pipe EOF ends.</summary>
    private static readonly TimeSpan PumpDrainGrace = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Runs <paramref name="executable"/> with <paramref name="arguments"/>, <paramref name="extraEnvironment"/> added to
    /// this process's environment, stdout and stderr written to <paramref name="logPath"/>. Cancelling the token kills the
    /// whole tree (Go <c>exec.CommandContext</c> killed the process). Throws <see cref="AcceptanceException"/> when the
    /// process cannot start; cancellation propagates as <see cref="OperationCanceledException"/> after the kill.
    /// </summary>
    internal static async Task<Exit> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> extraEnvironment,
        string logPath,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var pair in extraEnvironment)
        {
            info.Environment[pair.Key] = pair.Value;
        }

        var log = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        await using (log.ConfigureAwait(false))
        {
            using var process = new Process { StartInfo = info };
            try
            {
                if (!process.Start())
                {
                    throw new AcceptanceException($"{executable}: process did not start");
                }
            }
            catch (Win32Exception e)
            {
                throw new AcceptanceException($"start {executable}: {e.Message}", e);
            }

            var gate = new Lock();
            var pumps = new[]
            {
                PumpAsync(process.StandardOutput, log, gate),
                PumpAsync(process.StandardError, log, gate),
            };
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillTree(process);
                throw;
            }

            // The child's exit is the case's end. A game the child kept running (Game.KeepGameEnv) is created with handle
            // inheritance on (GameProcess.CreateDetached), so it holds a copy of the child's stdout/stderr pipes and the pumps
            // see no EOF until the game dies: a case that failed in 22s held its worker ~10 minutes (#3123). Drain what the
            // child wrote, then stop waiting for EOF.
            _ = await Task.WhenAny(Task.WhenAll(pumps), Task.Delay(PumpDrainGrace, CancellationToken.None)).ConfigureAwait(false);
            return new Exit(process.ExitCode);
        }
    }

    private static async Task PumpAsync(StreamReader source, StreamWriter log, Lock gate)
    {
        try
        {
            string? line;
            while ((line = await source.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                lock (gate)
                {
                    log.WriteLine(line);
                }
            }
        }
        catch (Exception e) when (e is ObjectDisposedException or IOException)
        {
            // The run ended and closed the log (or the pipe) while a surviving grandchild still held the pipe.
            return;
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        catch (Win32Exception)
        {
            // Access denied for a process that is exiting.
        }
    }

    /// <summary>Quotes nothing: the argv is passed as a list. This is the display form recorded in a report row.</summary>
    internal static string Describe(string executable, IReadOnlyList<string> arguments) =>
        string.Create(CultureInfo.InvariantCulture, $"{executable} {string.Join(' ', arguments)}");
}

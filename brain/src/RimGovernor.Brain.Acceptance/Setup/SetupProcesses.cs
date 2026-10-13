using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One row of the harness's process listing (Win32_Process; Go <c>setup.Process</c>): enough to tell whose game it is
/// (Path, CommandLine) and whether it is doing anything (WorkingSet, CPU against Started).
/// </summary>
public sealed class SetupProcess
{
    public int Pid { get; init; }

    public string Name { get; init; } = "";

    /// <summary>The executable path; empty when Windows withholds it.</summary>
    public string Path { get; init; } = "";

    /// <summary>The full command line (the game's -savedatafolder names its profile); empty when Windows withholds it.</summary>
    public string CommandLine { get; init; } = "";

    /// <summary>The working set in bytes.</summary>
    public ulong WorkingSet { get; init; }

    /// <summary>The kernel plus user time consumed so far.</summary>
    public TimeSpan Cpu { get; init; }

    /// <summary>The creation time; null when unreadable.</summary>
    public DateTimeOffset? Started { get; init; }

    /// <summary>
    /// Whether the process's executable or command line names a path under <paramref name="dir"/> (case-insensitive, as
    /// Windows paths are). Both sides are compared with forward slashes.
    /// </summary>
    public bool Under(string dir)
    {
        ArgumentNullException.ThrowIfNull(dir);
        var prefix = Slashed(dir).TrimEnd('/');
        return Slashed(Path).Contains(prefix, StringComparison.OrdinalIgnoreCase)
            || Slashed(CommandLine).Contains(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string Slashed(string path) => path.Replace('\\', '/');
}

/// <summary>
/// Lists and stops the games running from a private copy (Go <c>setup/process.go</c>). The brain runs inside the mod,
/// so the only harness image left is the game itself; there is no <c>rimgovernor.exe</c> child to find.
/// </summary>
public static class SetupProcesses
{
    /// <summary>The images a root launches (Go <c>HarnessImages</c>): only the game now.</summary>
    public static readonly IReadOnlyList<string> HarnessImages = [SetupDiscovery.GameImage];

    /// <summary>
    /// Lists the running processes of the named images (every one on the machine, peers' included; callers narrow by
    /// path).
    /// </summary>
    public static async Task<IReadOnlyList<SetupProcess>> ListProcessesAsync(IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(names);
        var filter = string.Join(" OR ", names.Select(n => $"Name='{n}'"));
        var script = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; "
            + "Get-CimInstance Win32_Process -Filter \"" + filter + "\" | ForEach-Object { "
            + "$t = 0; if ($_.KernelModeTime) { $t += $_.KernelModeTime }; if ($_.UserModeTime) { $t += $_.UserModeTime }; "
            + "$s = ''; if ($_.CreationDate) { $s = $_.CreationDate.ToUniversalTime().ToString('o') }; "
            + "\"$($_.ProcessId)`t$($_.Name)`t$($_.WorkingSetSize)`t$t`t$s`t$($_.ExecutablePath)`t$($_.CommandLine)\" }";
        var (exit, output, error) = await RunToolAsync("powershell", ["-NoProfile", "-NonInteractive", "-Command", script], cancellationToken).ConfigureAwait(false);
        if (exit != 0)
        {
            throw new SetupException(string.Create(CultureInfo.InvariantCulture, $"list processes [{string.Join(' ', names)}]: exit {exit}: {error.Trim()}"));
        }

        return ParseProcesses(output);
    }

    /// <summary>
    /// Reads the listing's tab-separated rows: pid, name, working set, CPU in 100 ns units, ISO start, executable path,
    /// command line (the last field keeps any tab of its own).
    /// </summary>
    internal static List<SetupProcess> ParseProcesses(string output)
    {
        var procs = new List<SetupProcess>();
        foreach (var raw in output.Split('\n'))
        {
            var fields = raw.TrimEnd('\r').Split('\t', 7);
            if (fields.Length < 7 || !int.TryParse(fields[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }

            _ = ulong.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var workingSet);
            var cpu = TimeSpan.Zero;
            if (long.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
            {
                cpu = TimeSpan.FromTicks(ticks);
            }

            DateTimeOffset? started = null;
            if (fields[4].Length > 0 && DateTimeOffset.TryParse(fields[4], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when))
            {
                started = when;
            }

            procs.Add(new SetupProcess
            {
                Pid = pid,
                Name = fields[1],
                Path = fields[5].Trim(),
                CommandLine = fields[6].Trim(),
                WorkingSet = workingSet,
                Cpu = cpu,
                Started = started,
            });
        }

        return procs;
    }

    /// <summary>
    /// Lists the PIDs of game processes whose executable lives under <paramref name="gameCopy"/>: this worktree's own
    /// games, never a peer's.
    /// </summary>
    public static async Task<IReadOnlyList<int>> RunningGamesAsync(string gameCopy, CancellationToken cancellationToken)
    {
        var procs = await RunningGameProcessesAsync(gameCopy, cancellationToken).ConfigureAwait(false);
        return [.. procs.Select(p => p.Pid)];
    }

    /// <summary>RunningGames with each process's full row.</summary>
    public static async Task<IReadOnlyList<SetupProcess>> RunningGameProcessesAsync(string gameCopy, CancellationToken cancellationToken)
    {
        var all = await ListProcessesAsync([SetupDiscovery.GameImage], cancellationToken).ConfigureAwait(false);
        return [.. all.Where(p => p.Under(gameCopy))];
    }

    /// <summary>
    /// Force-stops the given processes by pid and process tree, never by image name: peers' games run beside yours.
    /// Nothing to stop is not an error.
    /// </summary>
    public static void StopPids(IReadOnlyList<int> pids)
    {
        ArgumentNullException.ThrowIfNull(pids);
        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    /// <summary>
    /// Stops every game process running from <paramref name="gameCopy"/> by pid and waits until none is left, so a
    /// rebuilt mod can be installed under nobody. Returns the pids it stopped; none running is not an error.
    /// </summary>
    public static async Task<IReadOnlyList<int>> StopGamesAsync(string gameCopy, CancellationToken cancellationToken)
    {
        var pids = await RunningGamesAsync(gameCopy, cancellationToken).ConfigureAwait(false);
        if (pids.Count == 0)
        {
            return [];
        }

        StopPids(pids);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var left = await RunningGamesAsync(gameCopy, cancellationToken).ConfigureAwait(false);
            if (left.Count == 0)
            {
                return pids;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new SetupException(string.Create(CultureInfo.InvariantCulture, $"pids [{string.Join(' ', left)}] still run from {gameCopy} after the stop"));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Runs a console program to completion and returns its exit code and captured output.</summary>
    internal static async Task<(int Exit, string Stdout, string Stderr)> RunToolAsync(string exe, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        using var process = new Process { StartInfo = info };
        try
        {
            _ = process.Start();
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new SetupException($"start {exe}: {e.Message}", e);
        }

        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }
}

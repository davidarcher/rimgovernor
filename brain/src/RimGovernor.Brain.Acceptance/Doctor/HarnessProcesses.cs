using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One row of the harness's process listing (Go <c>setup.Process</c>): enough to tell whose game it is (Path,
/// CommandLine) and whether it is doing anything (WorkingSet, Cpu against Started).
/// </summary>
public sealed record HarnessProcess(int Pid, string Name, string Path, string CommandLine, ulong WorkingSet, TimeSpan Cpu, DateTimeOffset? Started)
{
    /// <summary>
    /// Whether the executable or command line names a path under <paramref name="dir"/> (case-insensitive; both sides
    /// compared with forward slashes).
    /// </summary>
    public bool Under(string dir)
    {
        string prefix = Slashed(System.IO.Path.GetFullPath(dir)).ToUpperInvariant();
        return Slashed(Path).ToUpperInvariant().Contains(prefix, StringComparison.Ordinal)
            || Slashed(CommandLine).ToUpperInvariant().Contains(prefix, StringComparison.Ordinal);
    }

    private static string Slashed(string path) => path.Replace('\\', '/');
}

/// <summary>The harness's process queries (Go <c>setup/process.go</c>): list by image, narrow to a game copy, stop by pid.</summary>
public static class HarnessProcesses
{
    /// <summary>The game's executable and the install marker.</summary>
    public const string GameImage = "RimWorld.exe";

    /// <summary>The images a root launches: the game (the serve binary of the Go harness no longer exists).</summary>
    public static IReadOnlyList<string> HarnessImages { get; } = [GameImage];

    /// <summary>Lists the running processes of the named images (every one on the machine, peers' included; callers narrow by path).</summary>
    public static async Task<IReadOnlyList<HarnessProcess>> ListAsync(IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(names);
        string filter = string.Join(" OR ", names.Select(n => $"Name='{n}'"));
        string script = "Get-CimInstance Win32_Process -Filter \"" + filter + "\" | ForEach-Object { "
            + "$t = 0; if ($_.KernelModeTime) { $t += $_.KernelModeTime }; if ($_.UserModeTime) { $t += $_.UserModeTime }; "
            + "$s = ''; if ($_.CreationDate) { $s = $_.CreationDate.ToUniversalTime().ToString('o') }; "
            + "\"$($_.ProcessId)`t$($_.Name)`t$($_.WorkingSetSize)`t$t`t$s`t$($_.ExecutablePath)`t$($_.CommandLine)\" }";
        string output = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        return Parse(output);
    }

    /// <summary>Reads <see cref="ListAsync"/>'s tab-separated rows: pid, name, working set, CPU in 100 ns units, ISO start, executable path, command line.</summary>
    internal static IReadOnlyList<HarnessProcess> Parse(string output)
    {
        var procs = new List<HarnessProcess>();
        foreach (string raw in output.Split('\n'))
        {
            string[] fields = raw.TrimEnd('\r').Split('\t', 7);
            if (fields.Length < 7 || !int.TryParse(fields[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
            {
                continue;
            }

            _ = ulong.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong workingSet);
            TimeSpan cpu = long.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks) ? TimeSpan.FromTicks(ticks) : TimeSpan.Zero;
            DateTimeOffset? started = fields[4].Length > 0 && DateTimeOffset.TryParse(fields[4], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
            procs.Add(new HarnessProcess(pid, fields[1], fields[5].Trim(), fields[6].Trim(), workingSet, cpu, started));
        }

        return procs;
    }

    /// <summary>This root's own games: GameImage processes whose executable lives under <paramref name="gameCopy"/>, never a peer's.</summary>
    public static async Task<IReadOnlyList<HarnessProcess>> RunningGameProcessesAsync(string gameCopy, CancellationToken cancellationToken)
    {
        IReadOnlyList<HarnessProcess> all = await ListAsync([GameImage], cancellationToken).ConfigureAwait(false);
        return [.. all.Where(p => p.Under(gameCopy))];
    }

    /// <summary>Force-stops the given processes and their trees by pid, never by image name (peers' games run beside yours). Nothing to stop is not an error.</summary>
    public static void StopPids(IReadOnlyList<int> pids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pids);
        foreach (int pid in pids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = GameProcess.KillTree(pid);
        }
    }

    private static async Task<string> RunPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("powershell")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);
        try
        {
            using var proc = Process.Start(psi) ?? throw new AcceptanceException("list processes: powershell did not start");
            string text = await proc.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (proc.ExitCode != 0)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"list processes: powershell exited {proc.ExitCode}"));
            }

            return text;
        }
        catch (Win32Exception e)
        {
            throw new AcceptanceException("list processes: " + e.Message, e);
        }
    }
}

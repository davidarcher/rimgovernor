using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// A harness process (<see cref="HarnessProcesses.HarnessImages"/>) launched under a <c>.claude/worktrees/&lt;name&gt;/</c>
/// that git no longer lists: the worktree went with its root, so <c>acceptance stop -root</c> cannot reach it, and the
/// game runs detached from the run that launched it (Go <c>doctor.Orphan</c>).
/// </summary>
public sealed record Orphan(HarnessProcess Process, string Worktree);

/// <summary>The doctor's orphan sweep and stuck-boot detection (Go <c>doctor/orphans.go</c>).</summary>
public static class Orphans
{
    /// <summary>A game that has run longer than this with a small working set and a pegged core never loaded a world.</summary>
    public static readonly TimeSpan StuckBootAge = TimeSpan.FromMinutes(5);

    /// <summary>Working-set bound (~100 MB seen; a loaded colony sits at 700 MB and above).</summary>
    public const ulong StuckBootWorkingSet = 250UL << 20;

    /// <summary>The share of wall time spent on a core that marks a spinning boot.</summary>
    public const double StuckBootCpuShare = 0.8;

    private static readonly string WorktreesDir = Path.DirectorySeparatorChar + Path.Combine(".claude", "worktrees") + Path.DirectorySeparatorChar;

    /// <summary>
    /// Picks, from <paramref name="procs"/>, those under a worktree that <paramref name="known"/> does not list;
    /// <paramref name="known"/> returns the worktrees of a main checkout (keyed by <see cref="WorktreeKey"/>), and null
    /// leaves that checkout's processes alone (never stop what cannot be classified).
    /// </summary>
    public static async Task<IReadOnlyList<Orphan>> SelectAsync(
        IReadOnlyList<HarnessProcess> procs,
        Func<string, Task<IReadOnlySet<string>?>> known)
    {
        ArgumentNullException.ThrowIfNull(procs);
        ArgumentNullException.ThrowIfNull(known);
        var cache = new Dictionary<string, IReadOnlySet<string>?>(StringComparer.Ordinal);
        var found = new List<Orphan>();
        foreach (HarnessProcess p in procs)
        {
            string wt = WorktreeOf(p);
            if (wt.Length == 0)
            {
                continue;
            }

            string main = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(wt))) ?? "";
            string key = main.ToUpperInvariant();
            if (!cache.TryGetValue(key, out IReadOnlySet<string>? list))
            {
                list = await known(main).ConfigureAwait(false);
                cache[key] = list;
            }

            if (list is null || list.Contains(WorktreeKey(wt)))
            {
                continue;
            }

            found.Add(new Orphan(p, wt));
        }

        found.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.Worktree, b.Worktree);
            return c != 0 ? c : a.Process.Pid.CompareTo(b.Process.Pid);
        });
        return found;
    }

    /// <summary>How known worktree paths are compared: cleaned, backslashed, upper-cased.</summary>
    public static string WorktreeKey(string path) => Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar)).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();

    /// <summary>The <c>&lt;main&gt;/.claude/worktrees/&lt;name&gt;</c> enclosing any path the process names, empty when none does.</summary>
    public static string WorktreeOf(HarnessProcess p)
    {
        ArgumentNullException.ThrowIfNull(p);
        foreach (string raw in new[] { p.Path, p.CommandLine })
        {
            string s = raw.Replace('/', Path.DirectorySeparatorChar);
            int i = s.IndexOf(WorktreesDir, StringComparison.OrdinalIgnoreCase);
            if (i < 0)
            {
                continue;
            }

            // The command line may quote or prefix the path; the segment before .claude is the main checkout, which ends
            // at the last separator or quote before it.
            int start = s.AsSpan(0, i).LastIndexOfAny("\"' ") + 1;
            string rest = s[(i + WorktreesDir.Length)..];
            string name = rest.Split(Path.DirectorySeparatorChar)[0].Split('"')[0];
            if (name.Length == 0)
            {
                continue;
            }

            return Path.Combine(s[start..i], ".claude", "worktrees", name);
        }

        return "";
    }

    /// <summary><c>git worktree list --porcelain</c> from <paramref name="main"/>, keyed by <see cref="WorktreeKey"/>; null when git fails.</summary>
    public static async Task<IReadOnlySet<string>?> GitWorktreesAsync(string main, CancellationToken cancellationToken)
    {
        (int exit, string output) = await DoctorGit.RunAsync(main, ["worktree", "list", "--porcelain"], TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        if (exit != 0)
        {
            return null;
        }

        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("worktree ", StringComparison.Ordinal))
            {
                _ = known.Add(WorktreeKey(trimmed["worktree ".Length..]));
            }
        }

        return known;
    }

    /// <summary>Lists every harness process orphaned by a removed worktree on this machine.</summary>
    public static async Task<IReadOnlyList<Orphan>> FindAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<HarnessProcess> procs = await HarnessProcesses.ListAsync(HarnessProcesses.HarnessImages, cancellationToken).ConfigureAwait(false);
        return await SelectAsync(procs, main => GitWorktreesAsync(main, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Whether <paramref name="p"/> looks like a boot that never finished, as of <paramref name="now"/>.</summary>
    public static bool StuckBoot(HarnessProcess p, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (p.Started is not { } started || p.WorkingSet == 0 || p.WorkingSet >= StuckBootWorkingSet)
        {
            return false;
        }

        TimeSpan age = now - started;
        return age >= StuckBootAge && p.Cpu.TotalSeconds >= StuckBootCpuShare * age.TotalSeconds;
    }

    /// <summary>The doctor's sweep: Warn naming each orphan by pid, image and vanished worktree, healed by stopping them.</summary>
    internal static async Task<DoctorCheck> CheckAsync(DoctorOptions o, CancellationToken cancellationToken)
    {
        var c = new DoctorCheck("orphans");
        IReadOnlyList<Orphan> found;
        try
        {
            found = await FindAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            c.Detail = "could not list harness processes: " + e.Message;
            return c;
        }

        if (found.Count == 0)
        {
            c.Detail = "no game process from a removed worktree";
            return c;
        }

        c.Status = DoctorStatus.Warn;
        c.Code = DoctorHealCodes.Orphans;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var lines = new List<string>();
        foreach (Orphan p in found)
        {
            string line = string.Create(CultureInfo.InvariantCulture, $"pid {p.Process.Pid} {p.Process.Name} from {Path.GetFileName(p.Worktree)}");
            if (StuckBoot(p.Process, now))
            {
                line += " (stuck boot)";
            }

            lines.Add(line);
            c.Pids.Add(p.Process.Pid);
        }

        c.Detail = string.Create(CultureInfo.InvariantCulture, $"{found.Count} processes outlive their removed worktrees: {string.Join("; ", lines)}");
        c.Fix = $"`acceptance doctor -root {o.Root} -heal` stops them by pid (a run's preflight does the same unless -no-heal)";
        return c;
    }

    /// <summary>The process check's addendum for this root's own games that look like a boot that never finished.</summary>
    internal static string StuckNote(IReadOnlyList<HarnessProcess> procs, DateTimeOffset now)
    {
        var pids = procs.Where(p => StuckBoot(p, now)).Select(p => p.Pid).ToList();
        if (pids.Count == 0)
        {
            return "";
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"; pids {DoctorText.Join(pids)} sit under {StuckBootWorkingSet >> 20} MB with a core pegged for over {StuckBootAge}: a boot that never finished");
    }
}

/// <summary>Runs git for the doctor's checks, bounded by a timeout.</summary>
internal static class DoctorGit
{
    /// <summary>Runs <c>git -C &lt;repo&gt; args</c>; the exit code is -1 when git is missing, cancelled by the timeout or fails to start.</summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(string repo, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("git")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(repo);
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(timeout);
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return (-1, "");
            }

            Task<string> stderr = proc.StandardError.ReadToEndAsync(bound.Token);
            string output = await proc.StandardOutput.ReadToEndAsync(bound.Token).ConfigureAwait(false);
            _ = await stderr.ConfigureAwait(false);
            await proc.WaitForExitAsync(bound.Token).ConfigureAwait(false);
            return (proc.ExitCode, output.Trim());
        }
        catch (Win32Exception)
        {
            return (-1, "");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (-1, "");
        }
    }
}

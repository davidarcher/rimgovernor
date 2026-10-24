using System.Globalization;
using System.Text.RegularExpressions;
using RimGovernor.Tools.Common;

namespace RimGovernor.Tools.Workflow.Commands;

/// <summary>The landing lane's guard against a merged tree that undoes main's recent landings.</summary>
internal static partial class LandChecks
{
    /// <summary>How many main landings <see cref="RefuseRevertsAsync"/> looks back through.</summary>
    private const int RevertWindow = 300;

    private const string NullBlob = "0000000000000000000000000000000000000000";

    [GeneratedRegex(@"\b[0-9a-f]{7,40}\b", RegexOptions.CultureInvariant)]
    private static partial Regex HashToken();

    [GeneratedRegex("[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex DigitRun();

    /// <summary>
    /// Refuses a merged branch that puts a path back to the content it had before one of main's last
    /// <see cref="RevertWindow"/> landings touched it: a stale tree committed over a newer main. The fork point alone
    /// cannot see this, since a stale tree re-parented onto main (git reset --soft main, then commit) forks at main
    /// itself. Deleting a file counts only for files main added after the branch forked (the oldest main landing any
    /// branch commit has as a parent; later merges of main do not move it), so the branch's own deletions of old
    /// files land. A branch commit message naming the reverted commit's hash marks the revert as intended.
    /// </summary>
    public static async Task RefuseRevertsAsync(string worktree, string main, CancellationToken ct)
    {
        var changed = await Git.RunAsync(worktree, ct, "diff", "--raw", "--no-abbrev", "--no-renames", main, "HEAD").ConfigureAwait(false);
        if (changed.Length == 0)
        {
            return;
        }
        var branchBlob = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in changed.Split('\n'))
        {
            if (RawEntry(line) is var (_, after, path))
            {
                branchBlob[path] = after;
            }
        }
        var history = await Git.RunAsync(
                worktree,
                ct,
                "log",
                "--first-parent",
                "--diff-merges=first-parent",
                "-n",
                RevertWindow.ToString(CultureInfo.InvariantCulture),
                "--raw",
                "--no-abbrev",
                "--no-renames",
                "--format=@%H %s",
                main)
            .ConfigureAwait(false);
        var branch = await Git.RunAsync(worktree, ct, "log", "--format=%P%x1f%B%x1e", main + "..HEAD").ConfigureAwait(false);
        var parents = new HashSet<string>(StringComparer.Ordinal);
        var named = new List<string>();
        foreach (var entry in branch.Split('\u001e'))
        {
            var split = entry.IndexOf('\u001f', StringComparison.Ordinal);
            var ps = split < 0 ? entry : entry[..split];
            var body = split < 0 ? "" : entry[(split + 1)..];
            foreach (var p in ps.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                parents.Add(p);
            }
            named.AddRange(HashToken().Matches(body).Select(m => m.Value));
        }
        // sinceFork holds main's landings newer than the fork: every one in the window down to the oldest that
        // parents a branch commit.
        var landings = new List<string>();
        foreach (var line in history.Split('\n'))
        {
            if (line.StartsWith('@'))
            {
                var header = line[1..];
                var space = header.IndexOf(' ', StringComparison.Ordinal);
                landings.Add(space < 0 ? header : header[..space]);
            }
        }
        var fork = landings.Count;
        for (var i = 0; i < landings.Count; i++)
        {
            if (parents.Contains(landings[i]))
            {
                fork = i;
            }
        }
        var sinceFork = new HashSet<string>(landings.Take(fork), StringComparer.Ordinal);
        bool Intended(string commit) => named.Any(token => commit.StartsWith(token, StringComparison.Ordinal));

        var reverted = new Dictionary<string, string>(StringComparer.Ordinal);
        var commit = "";
        var subject = "";
        foreach (var line in history.Split('\n'))
        {
            if (line.StartsWith('@'))
            {
                var header = line[1..];
                var space = header.IndexOf(' ', StringComparison.Ordinal);
                commit = space < 0 ? header : header[..space];
                subject = space < 0 ? "" : header[(space + 1)..];
                continue;
            }
            if (RawEntry(line) is not var (before, _, path))
            {
                continue;
            }
            if (!branchBlob.TryGetValue(path, out var blob) || reverted.ContainsKey(path) || blob != before)
            {
                continue;
            }
            if ((blob == NullBlob && !sinceFork.Contains(commit)) || Intended(commit))
            {
                continue;
            }
            if (blob != NullBlob && await CountOnlyAsync(worktree, main, path, ct).ConfigureAwait(false))
            {
                continue;
            }
            reverted[path] = $"{path} (undoes {commit[..Math.Min(9, commit.Length)]} {subject})";
        }
        if (reverted.Count == 0)
        {
            return;
        }
        var lines = reverted.Values.OrderBy(v => v, StringComparer.Ordinal);
        throw new ToolException(
            "the merged branch puts back content main replaced:\n  " + string.Join("\n  ", lines)
            + "\nrestore main's version (git checkout origin/main -- <path>) and commit, or name the commit's hash in a commit message if the revert is intended; then run land again");
    }

    /// <summary>
    /// Whether the branch's change to <paramref name="path"/> against main differs only in numbers: every removed line
    /// has an added twin once digit runs are masked. Adding a Concern bumps counts back to a value an earlier commit
    /// also held, so the blob equals an old pre-image without restoring any deleted behavior; a restored
    /// implementation changes more than digits.
    /// </summary>
    private static async Task<bool> CountOnlyAsync(string worktree, string main, string path, CancellationToken ct)
    {
        var result = await Proc.CaptureAsync(worktree, "git", ["diff", "-U0", "--no-color", "--no-renames", main, "HEAD", "--", path], ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return false;
        }
        var balance = new Dictionary<string, int>(StringComparer.Ordinal);
        var changes = 0;
        foreach (var line in result.Stdout.Split('\n'))
        {
            if (line.Length == 0 || line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }
            var masked = DigitRun().Replace(line[1..], "#");
            switch (line[0])
            {
                case '-':
                    balance[masked] = balance.GetValueOrDefault(masked) - 1;
                    changes++;
                    break;
                case '+':
                    balance[masked] = balance.GetValueOrDefault(masked) + 1;
                    changes++;
                    break;
            }
        }
        return changes > 0 && balance.Values.All(n => n == 0);
    }

    /// <summary>Parses one <c>--raw --no-abbrev</c> line into the blob before, the blob after and the path.</summary>
    private static (string Before, string After, string Path)? RawEntry(string line)
    {
        var tab = line.IndexOf('\t', StringComparison.Ordinal);
        if (tab < 0 || !line.StartsWith(':'))
        {
            return null;
        }
        var fields = line[..tab].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return fields.Length < 5 ? null : (fields[2], fields[3], line[(tab + 1)..].TrimEnd('\r'));
    }
}

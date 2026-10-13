using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RimGovernor.Tools.Common;

namespace RimGovernor.Tools.Commands;

/// <summary>The landing lane's guards: worktree discovery, the dirty-main refusal, main sync and the stale-revert refusal.</summary>
internal static partial class LandChecks
{
    /// <summary>How many main landings <see cref="RefuseRevertsAsync"/> looks back through.</summary>
    private const int RevertWindow = 300;

    private const string NullBlob = "0000000000000000000000000000000000000000";

    [GeneratedRegex(@"\b[0-9a-f]{7,40}\b", RegexOptions.CultureInvariant)]
    private static partial Regex HashToken();

    [GeneratedRegex("[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex DigitRun();

    /// <summary>One entry of <c>git worktree list --porcelain</c>; Branch is empty for a detached HEAD.</summary>
    internal sealed record Worktree(string Path, string Branch);

    /// <summary>One uncommitted path in the main checkout with what land could learn about who put it there.</summary>
    private sealed class DirtyPath(string status, string path)
    {
        public string Status { get; } = status; // porcelain XY

        public string Path { get; } = path; // repository-relative, slash-separated

        public DateTime? Modified { get; set; } // null when the file is gone

        public string Landed { get; set; } = ""; // main's last commit touching the path: "<hash> <date> <subject>"

        public List<string> Owners { get; } = []; // worktrees holding the same content
    }

    public static async Task<IReadOnlyList<Worktree>> ListWorktreesAsync(string repo, CancellationToken ct) =>
        ParseWorktrees(await Git.RunAsync(repo, ct, "worktree", "list", "--porcelain").ConfigureAwait(false));

    internal static List<Worktree> ParseWorktrees(string porcelain)
    {
        var result = new List<Worktree>();
        foreach (var raw in porcelain.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                result.Add(new Worktree(System.IO.Path.GetFullPath(line["worktree ".Length..]), ""));
            }
            else if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal) && result.Count > 0)
            {
                result[^1] = result[^1] with { Branch = line["branch refs/heads/".Length..] };
            }
        }
        return result;
    }

    /// <summary>
    /// Fetches origin/main and fast-forwards local main to it, so a landing never builds on a main that is behind
    /// what others have pushed. A main checkout (already verified clean) is moved with it. A local main holding
    /// unpushed landings that origin/main lacks is refused: the caller pushes or rebases them first.
    /// </summary>
    public static async Task SyncMainAsync(string worktree, string mainCheckout, CancellationToken ct)
    {
        var fetch = await Proc.CaptureAsync(worktree, "git", ["fetch", "origin", "main"], ct).ConfigureAwait(false);
        if (fetch.ExitCode != 0)
        {
            Log.Info($"origin/main not fetched (git fetch origin main: exit code {fetch.ExitCode}: {fetch.Stderr.Trim()}); landing on local main as it is");
            return;
        }
        if (!await Git.SucceedsAsync(worktree, ct, "rev-parse", "--verify", "-q", "refs/heads/main").ConfigureAwait(false))
        {
            // No local main (the main checkout is detached): origin/main is main.
            await Git.RunAsync(worktree, ct, "update-ref", "-m", "land: create from origin/main", "refs/heads/main", "origin/main").ConfigureAwait(false);
            Log.Info("local main created at origin/main");
            return;
        }
        if (await Git.SucceedsAsync(worktree, ct, "merge-base", "--is-ancestor", "origin/main", "main").ConfigureAwait(false))
        {
            return; // main already contains origin/main
        }
        if (!await Git.SucceedsAsync(worktree, ct, "merge-base", "--is-ancestor", "main", "origin/main").ConfigureAwait(false))
        {
            throw new ToolException("local main holds landings origin/main lacks and origin/main has moved too; rebase them (git rebase origin/main main), then run land again");
        }
        if (mainCheckout.Length > 0)
        {
            try
            {
                await Git.RunAsync(mainCheckout, ct, "merge", "--ff-only", "origin/main").ConfigureAwait(false);
            }
            catch (ToolException e)
            {
                throw new ToolException($"fast-forwarding the main checkout {mainCheckout} to origin/main: {e.Message}", e);
            }
        }
        else
        {
            await Git.RunAsync(worktree, ct, "update-ref", "-m", "land: fast-forward to origin/main", "refs/heads/main", "origin/main").ConfigureAwait(false);
        }
        Log.Info("main fast-forwarded to origin/main");
    }

    /// <summary>
    /// Reads <c>git status --porcelain -z</c> into XY and path pairs; a rename or copy carries its source as a second
    /// entry, which is skipped.
    /// </summary>
    private static List<DirtyPath> ParseStatusZ(string output)
    {
        var paths = new List<DirtyPath>();
        var entries = output.Split('\0');
        for (var i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            if (e.Length < 4)
            {
                continue;
            }
            paths.Add(new DirtyPath(e[..2], e[3..]));
            if (e[0] is 'R' or 'C')
            {
                i++;
            }
        }
        return paths;
    }

    /// <summary>
    /// Refuses a dirty main checkout, naming each dirty path, when it last changed, main's last landing on it and
    /// every worktree that holds the same content, so the operator can find the session that edited main directly.
    /// </summary>
    public static async Task RequireCleanMainAsync(string mainCheckout, IReadOnlyList<Worktree> trees, CancellationToken ct)
    {
        var output = await Git.OutputAsync(mainCheckout, ct, "status", "--porcelain", "-z", "--untracked-files=no").ConfigureAwait(false);
        var paths = ParseStatusZ(output);
        if (paths.Count == 0)
        {
            return;
        }
        foreach (var p in paths)
        {
            var full = Path.Combine(mainCheckout, p.Path.Replace('/', Path.DirectorySeparatorChar));
            byte[]? content = null;
            try
            {
                content = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The file is gone or unreadable.
            }
            if (File.Exists(full))
            {
                p.Modified = File.GetLastWriteTime(full);
            }
            var landed = await Proc.CaptureAsync(
                    mainCheckout,
                    "git",
                    ["log", "-1", "--format=%h %cd %s", "--date=format:%Y-%m-%d %H:%M:%S", "main", "--", p.Path],
                    ct)
                .ConfigureAwait(false);
            p.Landed = landed.ExitCode == 0 ? landed.Stdout.Trim() : "";
            if (content is null)
            {
                continue;
            }
            foreach (var wt in trees)
            {
                if (SamePath(wt.Path, mainCheckout))
                {
                    continue;
                }
                byte[] other;
                try
                {
                    other = await File.ReadAllBytesAsync(Path.Combine(wt.Path, p.Path.Replace('/', Path.DirectorySeparatorChar)), ct).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    continue;
                }
                if (!other.AsSpan().SequenceEqual(content))
                {
                    continue;
                }
                var where = await Git.SucceedsAsync(wt.Path, ct, "diff", "--quiet", "HEAD", "--", p.Path).ConfigureAwait(false)
                    ? "committed at its HEAD"
                    : "uncommitted there";
                p.Owners.Add($"{wt.Path} ({(wt.Branch.Length == 0 ? "detached" : wt.Branch)}), {where}");
            }
        }
        throw new ToolException(DirtyMessage(mainCheckout, paths, DateTime.Now));
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The refusal for a dirty main checkout.</summary>
    private static string DirtyMessage(string dir, List<DirtyPath> paths, DateTime now)
    {
        var b = new StringBuilder();
        b.Append(CultureInfo.InvariantCulture, $"main checkout {dir} has uncommitted changes; land never edits it and no session may (AGENTS.md):\n");
        foreach (var p in paths)
        {
            b.Append(CultureInfo.InvariantCulture, $"  {p.Status} {p.Path}\n");
            if (p.Modified is { } modified)
            {
                b.Append(CultureInfo.InvariantCulture, $"      modified {modified:yyyy-MM-dd HH:mm:ss} ({Durations.Format(now - modified)} ago)\n");
            }
            else
            {
                b.Append("      file is gone\n");
            }
            if (p.Landed.Length > 0)
            {
                b.Append(CultureInfo.InvariantCulture, $"      main last changed it in {p.Landed}\n");
            }
            if (p.Owners.Count == 0 && p.Modified is not null)
            {
                b.Append("      no other worktree holds this content\n");
            }
            foreach (var o in p.Owners)
            {
                b.Append(CultureInfo.InvariantCulture, $"      same content in {o}\n");
            }
        }
        b.Append("the session that owns an edit moves it to its own worktree; a leftover is restored with git checkout -- <path> in the main checkout; then run land again");
        return b.ToString();
    }

    /// <summary>
    /// Refuses a merged branch that puts a path back to the content it had before one of main's last
    /// <see cref="RevertWindow"/> landings touched it: a stale tree committed over a newer main. The fork point alone
    /// cannot see this, since a stale tree re-parented onto main (git reset --soft main, then commit) forks at main
    /// itself. Deleting a file counts only for files main added after the branch forked (the oldest main landing any
    /// branch commit has as a parent; later merges of main do not move it), so the branch's own deletions of old
    /// files land. A branch commit message naming the reverted commit's hash marks the revert as intended.
    /// </summary>
    public static async Task RefuseRevertsAsync(string worktree, CancellationToken ct)
    {
        var changed = await Git.RunAsync(worktree, ct, "diff", "--raw", "--no-abbrev", "--no-renames", "main", "HEAD").ConfigureAwait(false);
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
                "main")
            .ConfigureAwait(false);
        var branch = await Git.RunAsync(worktree, ct, "log", "--format=%P%x1f%B%x1e", "main..HEAD").ConfigureAwait(false);
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
            if (blob != NullBlob && await CountOnlyAsync(worktree, path, ct).ConfigureAwait(false))
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
            + "\nrestore main's version (git checkout main -- <path>) and commit, or name the commit's hash in a commit message if the revert is intended; then run land again");
    }

    /// <summary>
    /// Whether the branch's change to <paramref name="path"/> against main differs only in numbers: every removed line
    /// has an added twin once digit runs are masked. Adding a Concern bumps counts back to a value an earlier commit
    /// also held, so the blob equals an old pre-image without restoring any deleted behavior; a restored
    /// implementation changes more than digits.
    /// </summary>
    private static async Task<bool> CountOnlyAsync(string worktree, string path, CancellationToken ct)
    {
        var result = await Proc.CaptureAsync(worktree, "git", ["diff", "-U0", "--no-color", "--no-renames", "main", "HEAD", "--", path], ct).ConfigureAwait(false);
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

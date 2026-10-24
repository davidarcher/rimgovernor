using System.Text;
using RimGovernor.Tools.Common;

namespace RimGovernor.Tools.Workflow.Commands;

/// <summary>
/// The landing lane: takes a task branch onto origin's main as one squash commit. Nothing is tested or built here;
/// the nightly validates main.
/// <code>RimGovernor.cmd land [-m msg | -F file]</code>
/// Run from the branch's worktree, which must be clean. Each attempt fetches origin/main and merges it into the branch
/// (conflicts confined to the gate baselines resolve themselves, see <see cref="BaselineMerge"/>; any other conflict
/// aborts the merge), refuses a merged tree that undoes one of main's recent landings
/// (<see cref="LandChecks.RefuseRevertsAsync"/>), commits the branch's tree onto origin/main as one squash and pushes
/// it. When origin's main moved meanwhile the push is rejected and the lane tries again, up to <see cref="Attempts"/>
/// times; there is no lock, so landings run side by side and only a lost push race costs a retry. The branch is then
/// reset to the squash. A closing keyword in the message (Fixes #N) closes the issue on GitHub's side.
/// </summary>
internal static class LandCommand
{
    /// <summary>How many times the lane merges, squashes and pushes before giving up on a main that keeps moving.</summary>
    internal const int Attempts = 10;

    private const string Usage = "usage: land [-m msg | -F file]";

    /// <summary>One landing's inputs.</summary>
    internal sealed record Options
    {
        /// <summary>Any directory inside the branch's worktree.</summary>
        public required string Worktree { get; init; }

        /// <summary>The squash subject and body; empty takes the branch tip's message.</summary>
        public string Message { get; init; } = "";

        /// <summary>Called with the attempt number just before each push (tests move origin's main here).</summary>
        public Func<int, Task>? BeforePush { get; init; }
    }

    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var flags = Flags.Parse(args, new HashSet<string>(), new HashSet<string> { "m", "F" });
        if (flags.HelpRequested)
        {
            Log.Info(Usage);
            Log.Info("  -m  squash commit subject and body (default: the branch tip's message; Co-Authored-By trailers are appended)");
            Log.Info("  -F  file holding the squash commit subject and body");
            return 0;
        }
        if (flags.Positional.Count > 0)
        {
            throw new UsageException(Usage);
        }
        var message = flags.String("m");
        if (flags.String("F") is { Length: > 0 } file)
        {
            message = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
        }
        await LandAsync(new Options { Worktree = Directory.GetCurrentDirectory(), Message = message }, ct).ConfigureAwait(false);
        return 0;
    }

    internal static async Task LandAsync(Options options, CancellationToken ct)
    {
        var worktree = Repo.Require(options.Worktree);
        var branch = await Git.RunAsync(worktree, ct, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
        if (branch is "main" or "HEAD")
        {
            throw new ToolException($"{branch} is not a task branch");
        }
        var status = await Git.RunAsync(worktree, ct, "status", "--porcelain", "--untracked-files=no").ConfigureAwait(false);
        if (status.Length > 0)
        {
            throw new ToolException($"branch worktree {worktree} has uncommitted changes:\n{status}");
        }

        for (var attempt = 1; ; attempt++)
        {
            var fetch = await Proc.CaptureAsync(worktree, "git", ["fetch", "-q", "origin", "main"], ct).ConfigureAwait(false);
            if (fetch.ExitCode != 0)
            {
                throw new ToolException($"fetching origin's main failed; nothing landed: git fetch exited {fetch.ExitCode}: {fetch.Stderr.Trim()}");
            }
            var main = await Git.RunAsync(worktree, ct, "rev-parse", "origin/main").ConfigureAwait(false);
            var merge = await Proc.CaptureAsync(worktree, "git", ["merge", "--no-edit", main], ct).ConfigureAwait(false);
            if (merge.ExitCode != 0 && !await BaselineMerge.ResolveAsync(worktree, ct).ConfigureAwait(false))
            {
                await Proc.CaptureAsync(worktree, "git", ["merge", "--abort"], ct).ConfigureAwait(false);
                throw new ToolException(
                    $"merging main into {branch}: git merge exited {merge.ExitCode}: {merge.Stderr.Trim()}\nresolve the conflict on the branch (git merge origin/main), commit, and run land again");
            }
            await LandChecks.RefuseRevertsAsync(worktree, main, ct).ConfigureAwait(false);
            if (await Git.SucceedsAsync(worktree, ct, "diff", "--quiet", main).ConfigureAwait(false))
            {
                Log.Info($"{branch} has nothing to land: its tree matches main");
                return;
            }

            var body = await SquashMessageAsync(worktree, branch, main, options.Message, ct).ConfigureAwait(false);
            var squash = await CommitSquashAsync(worktree, main, body, ct).ConfigureAwait(false);
            if (options.BeforePush is { } beforePush)
            {
                await beforePush(attempt).ConfigureAwait(false);
            }
            if (await PushAsync(worktree, squash, ct).ConfigureAwait(false))
            {
                await Git.RunAsync(worktree, ct, "reset", "-q", "--hard", squash).ConfigureAwait(false);
                var landed = await Git.RunAsync(worktree, ct, "rev-parse", "--short", squash).ConfigureAwait(false);
                Log.Info($"landed {branch} on origin's main as {landed}; the branch is reset to it");
                return;
            }
            if (attempt >= Attempts)
            {
                throw new ToolException($"origin's main moved during each of {Attempts} attempts; nothing landed, run land again");
            }
            Log.Info("origin's main moved before the push; landing again on the new main");
        }
    }

    /// <summary>The branch's tree as one commit on <paramref name="main"/>.</summary>
    private static async Task<string> CommitSquashAsync(string worktree, string main, string body, CancellationToken ct)
    {
        var messagePath = Path.Combine(Path.GetTempPath(), $"land-{Environment.ProcessId}-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(messagePath, body, new UTF8Encoding(false), ct).ConfigureAwait(false);
        try
        {
            return await Git.RunAsync(worktree, ct, "commit-tree", "HEAD^{tree}", "-p", main, "-F", messagePath).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(messagePath);
        }
    }

    /// <summary>
    /// Pushes the squash to origin's main. True when origin took it, false when origin's main moved first (a
    /// non-fast-forward rejection, which the caller retries); any other failure throws, with nothing landed.
    /// </summary>
    private static async Task<bool> PushAsync(string worktree, string squash, CancellationToken ct)
    {
        var push = await Proc.CaptureAsync(worktree, "git", ["push", "--porcelain", "origin", squash + ":refs/heads/main"], ct).ConfigureAwait(false);
        if (push.ExitCode == 0)
        {
            return true;
        }
        // --porcelain marks a refused ref "!" and names a fast-forward refusal "[rejected]" (a hook or a protected
        // branch says "[remote rejected]").
        var rejected = push.Stdout.Split('\n').Any(line => line.StartsWith('!') && line.Contains("[rejected]", StringComparison.Ordinal));
        if (rejected)
        {
            return false;
        }
        throw new ToolException($"pushing to origin's main failed; nothing landed: git push exited {push.ExitCode}: {(push.Stdout + push.Stderr).Trim()}");
    }

    /// <summary>
    /// The squash message: the given one, else the branch's newest non-merge commit message; then every
    /// Co-Authored-By trailer on the branch's commits, once each.
    /// </summary>
    private static async Task<string> SquashMessageAsync(string worktree, string branch, string main, string message, CancellationToken ct)
    {
        var messages = await Git.OutputAsync(worktree, ct, "log", "--no-merges", "--format=%B%x1e", main + "..HEAD").ConfigureAwait(false);
        var commits = messages.Split('\u001e').Select(m => m.Trim()).Where(m => m.Length > 0).ToList();
        var trailers = new List<string>();
        string Strip(string m)
        {
            var kept = new List<string>();
            foreach (var line in m.Split('\n'))
            {
                var t = line.Trim();
                if (!t.StartsWith("Co-Authored-By:", StringComparison.Ordinal))
                {
                    kept.Add(line);
                }
                else if (!trailers.Contains(t))
                {
                    trailers.Add(t);
                }
            }
            return string.Join('\n', kept).Trim();
        }
        var stripped = commits.Select(Strip).ToList();
        var body = message.Trim().Length > 0 ? Strip(message) : stripped.FirstOrDefault() ?? "";
        if (body.Length == 0)
        {
            throw new ToolException($"no commit message on {branch} since main to title the squash; give -m");
        }
        return trailers.Count == 0 ? body + "\n" : body + "\n\n" + string.Join('\n', trailers) + "\n";
    }
}

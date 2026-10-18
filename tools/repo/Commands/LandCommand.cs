using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RimGovernor.Tools.Common;

namespace RimGovernor.Tools.Workflow.Commands;

/// <summary>
/// The serial landing lane: takes a task branch from its worktree onto main as one squash commit and pushes it, so
/// agents call it once instead of polling main, rebasing and retesting against every peer.
/// <code>RimGovernor.cmd land [flags] [branch]</code>
/// Run from anywhere inside the branch's worktree (the branch defaults to the checked-out one). In order it:
/// <list type="number">
/// <item>takes the repository-wide landing lock (one landing at a time; the lock is an open file handle, so a killed
/// lane never leaves it held);</item>
/// <item>fetches origin/main and fast-forwards local main to it (a diverged local main is refused; an unreachable
/// origin fails the landing), then merges main into the branch's worktree, which must be clean; a conflict aborts the
/// merge, and a merged tree that puts a path back to its content before one of main's recent landings is refused,
/// naming the paths and the landings;</item>
/// <item>checks the merged tree: -test runs the gate once on it, the landing's one check; without -test the branch
/// passed the gate on its own, so the lane only rebuilds (<see cref="Gate.BuildAsync(string, CancellationToken)"/>)
/// when the merge brought in files the gate builds (<see cref="Affected.GateOwed"/>; a docs-only merge does not),
/// undoing the merge if the build fails;</item>
/// <item>commits the merged branch's tree onto main as one squash commit, with a message built from the branch's
/// commits (-m or -F overrides the subject and body) carrying the branch's Co-Authored-By trailers, and pushes it to
/// origin's main. Local main moves only after origin accepted it, so the two never diverge. When origin's main moved
/// meanwhile the push is rejected and the lane fetches, merges and squashes again, up to <see cref="Attempts"/> times;
/// a worktree that has main checked out must be clean and is brought along after the move;</item>
/// <item>resets the branch to the new main when its tree is identical;</item>
/// <item>closes the GitHub issue the branch is for (the number in a branch name like claude/github-issue-128-abc, or
/// -issue N) with a comment naming the landing commit; -no-close skips it, and a missing gh or a failed close is
/// reported, never fatal.</item>
/// </list>
/// </summary>
internal static partial class LandCommand
{
    private const string LockName = "rimgovernor-land.lock";

    /// <summary>How many times the lane squashes and pushes before giving up on a main that keeps moving.</summary>
    internal const int Attempts = 3;

    private const string Usage = "usage: land [-m msg | -F file] [-lock-timeout d] [-test] [-issue N | -no-close] [<branch>]";

    [GeneratedRegex(@"(?:^|[/-])issue-(\d+)(?:-|$)", RegexOptions.CultureInvariant)]
    private static partial Regex IssueInBranch();

    /// <summary>One landing's inputs.</summary>
    internal sealed record Options
    {
        /// <summary>Any directory inside the branch's worktree.</summary>
        public required string Worktree { get; init; }

        /// <summary>The branch to land; empty means the checked-out one.</summary>
        public string Branch { get; init; } = "";

        public string Message { get; init; } = "";

        public string MessageFile { get; init; } = "";

        public TimeSpan LockTimeout { get; init; } = TimeSpan.FromMinutes(15);

        public TimeSpan LockPoll { get; init; } = TimeSpan.FromSeconds(5);

        public bool RunTests { get; init; }

        /// <summary>Called with the worktree, branch and short landing hash once the landing is on origin.</summary>
        public Func<string, string, string, Task>? Close { get; init; }

        /// <summary>Builds the merged tree in the worktree; null runs the gate's real builds (tests substitute a probe).</summary>
        public Func<string, Task>? Build { get; init; }

        /// <summary>Called with the attempt number just before each push (tests move origin's main here).</summary>
        public Func<int, Task>? BeforePush { get; init; }
    }

    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var flags = Flags.Parse(
            args,
            new HashSet<string> { "test", "no-close" },
            new HashSet<string> { "m", "F", "lock-timeout", "issue" });
        if (flags.HelpRequested)
        {
            Log.Info(Usage);
            Log.Info("  -m           squash commit subject and body (the trailers are appended)");
            Log.Info("  -F           file holding the squash commit subject and body");
            Log.Info("  -lock-timeout how long to wait for another landing to finish (default 15m)");
            Log.Info("  -test        run the gate once on the merged tree before landing (the landing's one check)");
            Log.Info("  -issue       GitHub issue to close with the landing commit (default: the number in the branch name)");
            Log.Info("  -no-close    do not close a GitHub issue");
            return 0;
        }
        if (flags.Positional.Count > 1)
        {
            throw new UsageException(Usage);
        }
        var issue = flags.Int("issue");
        await LandAsync(
                new Options
                {
                    Worktree = Directory.GetCurrentDirectory(),
                    Branch = flags.Positional.Count == 1 ? flags.Positional[0] : "",
                    Message = flags.String("m"),
                    MessageFile = flags.String("F"),
                    LockTimeout = flags.Duration("lock-timeout", TimeSpan.FromMinutes(15)),
                    RunTests = flags.Bool("test"),
                    Close = flags.Bool("no-close") ? null : (worktree, branch, landed) => CloseIssueAsync(issue, worktree, branch, landed, ct),
                },
                ct)
            .ConfigureAwait(false);
        return 0;
    }

    internal static async Task LandAsync(Options options, CancellationToken ct)
    {
        var worktree = Repo.Require(options.Worktree);
        var branch = options.Branch;
        var current = await Git.RunAsync(worktree, ct, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
        if (branch.Length == 0)
        {
            branch = current;
        }
        if (branch != current)
        {
            throw new ToolException($"this worktree has {current} checked out, not {branch}; run land from the branch's worktree");
        }
        if (branch is "main" or "HEAD")
        {
            throw new ToolException($"{branch} is not a task branch");
        }
        var trees = await LandChecks.ListWorktreesAsync(worktree, ct).ConfigureAwait(false);
        // land moves the main ref itself; a worktree that has main checked out is optional and only brought along
        // after the move.
        var mainCheckout = trees.LastOrDefault(wt => wt.Branch == "main")?.Path ?? "";
        var message = options.Message;
        if (options.MessageFile.Length > 0)
        {
            message = await File.ReadAllTextAsync(options.MessageFile, ct).ConfigureAwait(false);
        }

        var common = await Git.RunAsync(worktree, ct, "rev-parse", "--path-format=absolute", "--git-common-dir").ConfigureAwait(false);
        using var landingLock = await FileLock.AcquireAsync(
                Path.Combine(common, LockName),
                "branch=" + branch,
                options.LockTimeout,
                options.LockPoll,
                holder => Log.Info($"waiting for landing lock held by {holder}"),
                ct)
            .ConfigureAwait(false);

        await RequireCleanAsync(worktree, "branch worktree", ct).ConfigureAwait(false);
        if (mainCheckout.Length > 0)
        {
            await LandChecks.RequireCleanMainAsync(mainCheckout, trees, ct).ConfigureAwait(false);
        }
        var tested = false;
        string oldMain;
        string squash;
        for (var attempt = 1; ; attempt++)
        {
            await LandChecks.SyncMainAsync(worktree, mainCheckout, ct).ConfigureAwait(false);
            oldMain = await Git.RunAsync(worktree, ct, "rev-parse", "main").ConfigureAwait(false);
            var beforeMerge = await Git.RunAsync(worktree, ct, "rev-parse", "HEAD").ConfigureAwait(false);
            var merge = await Proc.CaptureAsync(worktree, "git", ["merge", "--no-edit", oldMain], ct).ConfigureAwait(false);
            if (merge.ExitCode != 0)
            {
                await Proc.CaptureAsync(worktree, "git", ["merge", "--abort"], ct).ConfigureAwait(false);
                throw new ToolException(
                    $"merging main into {branch}: git merge exited {merge.ExitCode}: {merge.Stderr.Trim()}\nresolve the conflict on the branch (git merge main), commit, and run land again");
            }
            await LandChecks.RefuseRevertsAsync(worktree, ct).ConfigureAwait(false);
            if (await Git.SucceedsAsync(worktree, ct, "diff", "--quiet", "main").ConfigureAwait(false))
            {
                Log.Info($"{branch} has nothing to land: its tree matches main");
                return;
            }
            if (options.RunTests && !tested)
            {
                var changed = await Affected.ChangedFilesAsync(worktree, "main", ct).ConfigureAwait(false);
                await Affected.TestAsync(worktree, changed, full: false, ct).ConfigureAwait(false);
                tested = true;
            }
            else
            {
                await BuildMergedInputsAsync(worktree, beforeMerge, options.Build, ct).ConfigureAwait(false);
            }

            var head = await Git.RunAsync(worktree, ct, "rev-parse", "HEAD").ConfigureAwait(false);
            var body = await SquashMessageAsync(worktree, branch, message, ct).ConfigureAwait(false);
            squash = await CommitSquashAsync(worktree, branch, head, oldMain, body, ct).ConfigureAwait(false);
            if (options.BeforePush is { } beforePush)
            {
                await beforePush(attempt).ConfigureAwait(false);
            }
            if (await PushAsync(worktree, squash, ct).ConfigureAwait(false))
            {
                break;
            }
            if (attempt >= Attempts)
            {
                throw new ToolException($"origin's main moved during each of {Attempts} attempts; nothing landed, run land again");
            }
            Log.Info("origin's main moved before the push; landing again on the new main");
        }

        var landed = await Git.RunAsync(worktree, ct, "rev-parse", "--short", squash).ConfigureAwait(false);
        Log.Info($"landed {branch} on origin's main as {landed}");
        // Origin has the squash; local main follows unless something else moved it under the lock, in which case the
        // next landing fast-forwards it from origin.
        if (!await Git.SucceedsAsync(worktree, ct, "update-ref", "-m", "land: " + branch, "refs/heads/main", squash, oldMain).ConfigureAwait(false))
        {
            Log.Info($"local main moved off {oldMain[..Math.Min(9, oldMain.Length)]} during the landing and was not updated; the next land fast-forwards it from origin");
        }
        else if (mainCheckout.Length > 0)
        {
            var readTree = await Proc.CaptureAsync(mainCheckout, "git", ["read-tree", "-m", "-u", oldMain, squash], ct).ConfigureAwait(false);
            if (readTree.ExitCode != 0)
            {
                Log.Info($"main checkout {mainCheckout} not updated to {landed} ({readTree.Stderr.Trim()}); bring it along with git reset --keep main there");
            }
        }
        // Catch the branch up so the identical-tree check below sees only what this landing missed.
        if (!await Git.SucceedsAsync(worktree, ct, "merge", "--no-edit", "main").ConfigureAwait(false))
        {
            await Git.SucceedsAsync(worktree, ct, "merge", "--abort").ConfigureAwait(false);
        }
        if (await Git.SucceedsAsync(worktree, ct, "diff", "--quiet", "main").ConfigureAwait(false))
        {
            await Git.RunAsync(worktree, ct, "reset", "--hard", "main").ConfigureAwait(false);
            Log.Info($"{branch} reset to main ({landed}); start the next task from here");
        }
        else
        {
            Log.Info($"{branch} left as is: its tree differs from the landed main");
        }
        if (options.Close is { } close)
        {
            await close(worktree, branch, landed).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Rebuilds the merged tree when merging main brought in files the gate builds (<see cref="Affected.GateOwed"/>):
    /// the branch passed the gate without them, and both sides passed it alone, so what is left to catch is the two
    /// not compiling together. The build takes seconds where the full gate takes a minute under the landing lock; a
    /// test-level interaction between the sides shows at the next landing's gate and the nightly. A merge that brought
    /// only docs lands without a rebuild. A failed build undoes the merge, so the next land merges and builds again
    /// instead of finding nothing new and landing the failing tree.
    /// </summary>
    private static async Task BuildMergedInputsAsync(string worktree, string beforeMerge, Func<string, Task>? build, CancellationToken ct)
    {
        var brought = await Git.LinesAsync(worktree, ct, "diff", "--name-only", beforeMerge, "HEAD").ConfigureAwait(false);
        if (!Affected.GateOwed(brought.Select(file => file.TrimEnd('\r')).Where(file => file.Length > 0)))
        {
            return;
        }
        Log.Info("main brought in files the gate builds; building the merged tree");
        try
        {
            await (build is null ? Gate.BuildAsync(worktree, ct) : build(worktree)).ConfigureAwait(false);
        }
        catch (ToolException e)
        {
            await Git.RunAsync(worktree, ct, "reset", "--hard", beforeMerge).ConfigureAwait(false);
            throw new ToolException(
                $"the merged tree does not build; nothing landed and the merge of main was undone: {e.Message}\n"
                + "merge main (git merge main), fix the failure, commit, and run land -test again");
        }
    }

    /// <summary>The merged branch's tree as one commit on <paramref name="oldMain"/>; no checkout of main is needed.</summary>
    private static async Task<string> CommitSquashAsync(string worktree, string branch, string head, string oldMain, string body, CancellationToken ct)
    {
        var messagePath = Path.Combine(Path.GetTempPath(), $"land-{Environment.ProcessId}-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(messagePath, body.TrimEnd('\n') + "\n", new UTF8Encoding(false), ct).ConfigureAwait(false);
        try
        {
            return await Git.RunAsync(worktree, ct, "commit-tree", head + "^{tree}", "-p", oldMain, "-F", messagePath).ConfigureAwait(false);
        }
        catch (ToolException e)
        {
            throw new ToolException($"committing the squash of {branch}: {e.Message}", e);
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

    /// <summary>Closes the GitHub issue a landed branch was for, or does nothing; it never fails the landing.</summary>
    private static async Task CloseIssueAsync(int issue, string worktree, string branch, string landed, CancellationToken ct)
    {
        var number = issue;
        if (number == 0)
        {
            var m = IssueInBranch().Match(branch);
            if (!m.Success)
            {
                return;
            }
            number = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        var numberText = number.ToString(CultureInfo.InvariantCulture);
        try
        {
            await Git.GhAsync(worktree, ct, "issue", "close", numberText, "--comment", $"Landed on main as {landed}.").ConfigureAwait(false);
        }
        catch (ToolException e)
        {
            Log.Info($"issue #{number} not closed ({e.Message}); close it by hand with the landing commit");
            return;
        }
        Log.Info($"closed issue #{number}");
    }

    private static async Task RequireCleanAsync(string dir, string what, CancellationToken ct)
    {
        var status = await Git.RunAsync(dir, ct, "status", "--porcelain", "--untracked-files=no").ConfigureAwait(false);
        if (status.Length > 0)
        {
            throw new ToolException($"{what} {dir} has uncommitted changes:\n{status}");
        }
    }

    /// <summary>
    /// Builds the squash commit message: the given message, or the branch's one titled commit message, or the newest
    /// title with the others listed; then the branch's Co-Authored-By trailers. Merge commits (more than one parent,
    /// whatever their subject) never title or fill the squash. A title is a non-merge commit subject that is not a
    /// placeholder such as "wip"; a branch with none needs -m.
    /// </summary>
    private static async Task<string> SquashMessageAsync(string worktree, string branch, string message, CancellationToken ct)
    {
        var messages = await Git.OutputAsync(worktree, ct, "log", "--no-merges", "--format=%B%x1e", "main..HEAD").ConfigureAwait(false);
        var commits = new List<string>();
        foreach (var m in messages.Split('\u001e'))
        {
            var trimmed = m.Trim();
            if (trimmed.Length > 0 && !Untitled(FirstLine(trimmed)))
            {
                commits.Add(trimmed);
            }
        }
        var trailers = new HashSet<string>(StringComparer.Ordinal);
        var trailerLines = new List<string>();
        string Strip(string m)
        {
            var kept = new List<string>();
            foreach (var line in m.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("Co-Authored-By:", StringComparison.Ordinal))
                {
                    if (trailers.Add(t))
                    {
                        trailerLines.Add(t);
                    }
                    continue;
                }
                kept.Add(line);
            }
            return string.Join('\n', kept).Trim();
        }
        var stripped = commits.Select(Strip).ToList();
        var body = message.Trim();
        if (body.Length > 0)
        {
            body = Strip(body);
        }
        else
        {
            switch (stripped.Count)
            {
                case 0:
                    throw new ToolException($"no titled commits on {branch} since main (only merges or placeholders like wip); give -m or reword the tip");
                case 1:
                    body = stripped[0];
                    break;
                default:
                    var lines = new List<string>
                    {
                        FirstLine(stripped[0]),
                        "",
                        string.Create(CultureInfo.InvariantCulture, $"Squashed from {stripped.Count} commits on {branch}:"),
                    };
                    lines.AddRange(stripped.Select(m => "- " + FirstLine(m)));
                    body = string.Join('\n', lines);
                    break;
            }
        }
        return trailerLines.Count == 0 ? body + "\n" : body + "\n\n" + string.Join('\n', trailerLines) + "\n";
    }

    /// <summary>True for a subject that cannot title a squash: autosquash markers and checkpoint placeholders.</summary>
    internal static bool Untitled(string subject)
    {
        var s = subject.Trim().ToLowerInvariant();
        string[] prefixes = ["fixup!", "squash!", "amend!"];
        if (prefixes.Any(p => s.StartsWith(p, StringComparison.Ordinal)))
        {
            return true;
        }
        var words = s.Split([' ', ':', '.', '('], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return true;
        }
        return words[0] is "wip" or "tmp" or "temp" or "checkpoint" or "fixup" or "squash" or "todo" or "xxx";
    }

    private static string FirstLine(string s)
    {
        var i = s.IndexOf('\n', StringComparison.Ordinal);
        return i < 0 ? s : s[..i];
    }
}

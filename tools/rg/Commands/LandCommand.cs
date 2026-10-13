using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RimGovernor.Tools.Common;

namespace RimGovernor.Tools.Commands;

/// <summary>
/// The serial landing lane: takes a task branch from its worktree onto local main as one squash commit, so agents
/// call it once instead of polling main, rebasing and retesting against every peer (Go: <c>cmd/land</c>).
/// <code>RimGovernor.cmd land [flags] [branch]</code>
/// Run from anywhere inside the branch's worktree (the branch defaults to the checked-out one). In order it:
/// <list type="number">
/// <item>takes the repository-wide landing lock (one landing at a time);</item>
/// <item>fetches origin/main and fast-forwards local main to it (a diverged local main is refused; an unreachable
/// origin is reported and the lane continues), then merges main into the branch's worktree, which must be clean; a
/// conflict aborts the merge, and a merged tree that puts a path back to its content before one of main's recent
/// landings is refused, naming the paths and the landings;</item>
/// <item>optionally runs tests with -test; callers normally run the test tool before landing;</item>
/// <item>commits the merged branch's tree onto main as one squash commit and moves the main ref only if main has not
/// moved since step 2, with a message built from the branch's commits (-m or -F overrides the subject and body)
/// carrying the branch's Co-Authored-By trailers; a worktree that has main checked out must be clean and is
/// brought along after the move;</item>
/// <item>resets the branch to the new main when its tree is identical;</item>
/// <item>closes the GitHub issue the branch is for (the number in a branch name like claude/github-issue-128-abc, or
/// -issue N) with a comment naming the landing commit; -no-close skips it, and a missing gh or a failed close is
/// reported, never fatal.</item>
/// </list>
/// </summary>
internal static partial class LandCommand
{
    private const string LockName = "rimgovernor-land.lock";

    private const string Usage = "usage: land [-m msg | -F file] [-lock-timeout d] [-test] [-issue N | -no-close] [<branch>]";

    [GeneratedRegex(@"(?:^|[/-])issue-(\d+)(?:-|$)", RegexOptions.CultureInvariant)]
    private static partial Regex IssueInBranch();

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
            Log.Info("  -test        also run the affected Go tests against the merged tree before landing");
            Log.Info("  -issue       GitHub issue to close with the landing commit (default: the number in the branch name)");
            Log.Info("  -no-close    do not close a GitHub issue");
            return 0;
        }
        if (flags.Positional.Count > 1)
        {
            throw new UsageException(Usage);
        }
        var issue = flags.Int("issue");
        var noClose = flags.Bool("no-close");
        await LandAsync(
                flags.Positional.Count == 1 ? flags.Positional[0] : "",
                flags.String("m"),
                flags.String("F"),
                flags.Duration("lock-timeout", TimeSpan.FromMinutes(15)),
                flags.Bool("test"),
                noClose ? null : (worktree, branch, landed) => CloseIssueAsync(issue, worktree, branch, landed, ct),
                ct)
            .ConfigureAwait(false);
        return 0;
    }

    private static async Task LandAsync(
        string branch,
        string message,
        string messageFile,
        TimeSpan lockTimeout,
        bool runTests,
        Func<string, string, string, Task>? close,
        CancellationToken ct)
    {
        var worktree = Repo.Require(Directory.GetCurrentDirectory());
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
        if (messageFile.Length > 0)
        {
            message = await File.ReadAllTextAsync(messageFile, ct).ConfigureAwait(false);
        }

        using var landingLock = await AcquireLockAsync(worktree, branch, lockTimeout, ct).ConfigureAwait(false);

        await RequireCleanAsync(worktree, "branch worktree", ct).ConfigureAwait(false);
        if (mainCheckout.Length > 0)
        {
            await LandChecks.RequireCleanMainAsync(mainCheckout, trees, ct).ConfigureAwait(false);
        }
        await LandChecks.SyncMainAsync(worktree, mainCheckout, ct).ConfigureAwait(false);
        var oldMain = await Git.RunAsync(worktree, ct, "rev-parse", "main").ConfigureAwait(false);
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
        var changed = await Affected.ChangedFilesAsync(worktree, "main", ct).ConfigureAwait(false);
        if (runTests)
        {
            await Affected.TestAsync(worktree, changed, full: false, ct).ConfigureAwait(false);
        }
        else
        {
            Log.Info("tests: not run by the lane (run the test tool before landing, or pass -test)");
        }

        var head = await Git.RunAsync(worktree, ct, "rev-parse", "HEAD").ConfigureAwait(false);
        var body = await SquashMessageAsync(worktree, branch, message, ct).ConfigureAwait(false);
        // The merged branch's tree is the squash: commit it onto the main land merged and move the ref only if main
        // is still there, so no checkout of main is needed and a concurrent move is refused, not overwritten.
        var messagePath = Path.Combine(Path.GetTempPath(), $"land-{Environment.ProcessId}.txt");
        await File.WriteAllTextAsync(messagePath, body.TrimEnd('\n') + "\n", new UTF8Encoding(false), ct).ConfigureAwait(false);
        string squash;
        try
        {
            try
            {
                squash = await Git.RunAsync(worktree, ct, "commit-tree", head + "^{tree}", "-p", oldMain, "-F", messagePath).ConfigureAwait(false);
            }
            catch (ToolException e)
            {
                throw new ToolException($"committing the squash of {branch}: {e.Message}", e);
            }
        }
        finally
        {
            File.Delete(messagePath);
        }
        try
        {
            await Git.RunAsync(worktree, ct, "update-ref", "-m", "land: " + branch, "refs/heads/main", squash, oldMain).ConfigureAwait(false);
        }
        catch (ToolException e)
        {
            throw new ToolException($"main moved off {oldMain[..Math.Min(9, oldMain.Length)]} while {branch} was landing; nothing landed, run land again: {e.Message}", e);
        }
        var landed = await Git.RunAsync(worktree, ct, "rev-parse", "--short", squash).ConfigureAwait(false);
        Log.Info($"landed {branch} on main as {landed}");
        if (mainCheckout.Length > 0)
        {
            var readTree = await Proc.CaptureAsync(mainCheckout, "git", ["read-tree", "-m", "-u", oldMain, squash], ct).ConfigureAwait(false);
            if (readTree.ExitCode != 0)
            {
                Log.Info($"main checkout {mainCheckout} not updated to {landed} ({readTree.Stderr.Trim()}); bring it along with git reset --keep main there");
            }
        }
        // main may have moved again while the tests ran; catch the branch up so the identical-tree check below sees
        // only what this landing missed.
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
        if (close is not null)
        {
            await close(worktree, branch, landed).ConfigureAwait(false);
        }
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
    /// Takes the repository-wide landing lock under the shared git directory, waiting for a holder to finish up to
    /// <paramref name="timeout"/>. Disposing releases it.
    /// </summary>
    private static async Task<IDisposable> AcquireLockAsync(string repo, string branch, TimeSpan timeout, CancellationToken ct)
    {
        var common = await Git.RunAsync(repo, ct, "rev-parse", "--path-format=absolute", "--git-common-dir").ConfigureAwait(false);
        var path = Path.Combine(common, LockName);
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
                {
                    writer.Write(string.Create(CultureInfo.InvariantCulture, $"pid={Environment.ProcessId} branch={branch} since={DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz}\n"));
                }
                return new LockHandle(stream, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                var holder = TryRead(path);
                if (deadline.Elapsed > timeout)
                {
                    throw new ToolException(
                        $"landing lock {path} held for over {Durations.Format(timeout)} by {holder}; remove it only if that process is gone");
                }
                Log.Info($"waiting for landing lock held by {holder}");
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
        }
    }

    private static string TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Trim();
        }
        catch (IOException)
        {
            return "another landing";
        }
    }

    private sealed class LockHandle(FileStream stream, string path) : IDisposable
    {
        public void Dispose()
        {
            stream.Dispose();
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A lingering lock names its holder; the next landing reports it.
            }
        }
    }

    /// <summary>
    /// Builds the squash commit message: the given message, or the branch's one titled commit message, or the newest
    /// title with the others listed; then the branch's Co-Authored-By trailers. A title is a commit subject, merges
    /// included (a merge renamed to the milestone subject titles the squash), that is neither git's default merge
    /// subject nor a placeholder such as "wip"; a branch with none needs -m.
    /// </summary>
    private static async Task<string> SquashMessageAsync(string worktree, string branch, string message, CancellationToken ct)
    {
        var messages = await Git.OutputAsync(worktree, ct, "log", "--format=%B%x1e", "main..HEAD").ConfigureAwait(false);
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

    /// <summary>True for a subject that cannot title a squash: git's default merge subjects and checkpoint placeholders.</summary>
    internal static bool Untitled(string subject)
    {
        var s = subject.Trim().ToLowerInvariant();
        string[] prefixes = ["merge branch ", "merge remote-tracking branch ", "merge commit ", "fixup!", "squash!", "amend!"];
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

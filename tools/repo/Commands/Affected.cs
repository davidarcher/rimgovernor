using System.Diagnostics;
using RimGovernor.Tools.Common;

namespace RimGovernor.Tools.Workflow.Commands;

/// <summary>
/// Finds the files a change touched and the checks worth running for them: the native contract probes build when its
/// inputs changed and the C# solutions' gate (<c>task build</c>, <c>task test</c>) when anything under them did. Also
/// the <c>affected</c> subcommand, which only reports what a change owes.
/// </summary>
internal static class Affected
{
    /// <summary>Roots whose files the native contract probes build compiles or links.</summary>
    private static readonly string[] ProbeInputs =
    [
        "integrations/rimgovernor-native/src",
        "contracts/tests",
    ];

    /// <summary>Roots of the C# projects the root Taskfile builds and tests.</summary>
    private static readonly string[] CSharpRoots =
    [
        "brain/", "tools/rg/", "tools/launcher/", "integrations/", "contracts/", "scripts/",
    ];

    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var flags = Flags.Parse(args, new HashSet<string>(), new HashSet<string> { "base" });
        if (flags.HelpRequested)
        {
            Log.Info("usage: affected [-base main]");
            Log.Info("Lists the files changed since the merge base and the checks they owe.");
            return 0;
        }
        var repo = Repo.Require(Directory.GetCurrentDirectory());
        var changed = await ChangedFilesAsync(repo, flags.String("base", "main"), ct).ConfigureAwait(false);
        Log.Info($"changed files: {changed.Count}");
        foreach (var file in changed)
        {
            Log.Info("  " + file);
        }
        Log.Info(ProbesChanged(changed) ? "probes: task probes:build (run: test)" : "probes: unaffected");
        Log.Info(CSharpChanged(changed) ? "csharp: task build and task test (run: test)" : "csharp: unaffected");
        return 0;
    }

    /// <summary>
    /// Lists the repo-relative files the working tree changed since it diverged from <paramref name="baseRev"/> (the
    /// merge base, so what base gained meanwhile does not count), including uncommitted and untracked ones.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ChangedFilesAsync(string repo, string baseRev, CancellationToken ct)
    {
        var mergeBase = await Git.LinesAsync(repo, ct, "merge-base", baseRev, "HEAD").ConfigureAwait(false);
        if (mergeBase.Count == 0)
        {
            throw new ToolException($"no merge base between {baseRev} and HEAD");
        }
        var committed = await Git.LinesAsync(repo, ct, "diff", "--name-only", mergeBase[0]).ConfigureAwait(false);
        var untracked = await Git.LinesAsync(repo, ct, "ls-files", "--others", "--exclude-standard").ConfigureAwait(false);
        var files = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in committed.Concat(untracked))
        {
            var name = file.TrimEnd('\r');
            if (name.Length > 0)
            {
                files.Add(name);
            }
        }
        return [.. files];
    }

    /// <summary>
    /// Runs the checks the changed files owe: the native contract probes build when a probe input changed, then
    /// <c>task build</c> and <c>task test</c> when any C# project changed (or <paramref name="full"/> asks for the lot).
    /// </summary>
    public static async Task TestAsync(string repo, IReadOnlyList<string> changed, bool full, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        try
        {
            await TestCoreAsync(repo, changed, full, ct).ConfigureAwait(false);
        }
        catch (ToolException)
        {
            Log.Info($"test: FAIL ({Durations.Format(started.Elapsed)})");
            throw;
        }
        Log.Info($"test: PASS ({Durations.Format(started.Elapsed)})");
    }

    private static async Task TestCoreAsync(string repo, IReadOnlyList<string> changed, bool full, CancellationToken ct)
    {
        if (!full && !CSharpChanged(changed))
        {
            Log.Info("tests: no C# project changed, nothing to test");
            return;
        }
        Log.Info("tests: task build, then task test (a project prints only when it finishes; silence is normal)");
        await Proc.RunAsync(repo, "task", ["build"], ct).ConfigureAwait(false);
        await Proc.RunAsync(repo, "task", ["test"], ct).ConfigureAwait(false);
    }

    public static bool ProbesChanged(IEnumerable<string> changed) =>
        changed.Any(file => ProbeInputs.Any(root => file.Replace('\\', '/').StartsWith(root + "/", StringComparison.Ordinal)));

    public static bool CSharpChanged(IEnumerable<string> changed) =>
        changed.Any(file => CSharpRoots.Any(root => file.Replace('\\', '/').StartsWith(root, StringComparison.Ordinal)));
}

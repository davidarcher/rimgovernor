using System.Diagnostics;
using RimGovernor.Tools.Common;

namespace RimGovernor.Tools.Workflow.Commands;

/// <summary>
/// Finds the files a change touched and whether they owe the gate (<see cref="Gate"/>). Also the <c>affected</c>
/// subcommand, which only reports what a change owes.
/// </summary>
internal static class Affected
{
    /// <summary>What the gate builds and tests: a change elsewhere (docs, the issue tracker's notes) owes no gate.</summary>
    private static readonly string[] GateInputs =
    [
        "brain/", "tools/", "integrations/", "contracts/", "scripts/", "RimGovernor.slnx", "RimGovernor.cmd", "dotnet-tools.json",
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
        Log.Info(GateOwed(changed) ? "gate: owed (run: test)" : "gate: unaffected");
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

    /// <summary>Runs the gate when the changed files owe it (or <paramref name="full"/> asks for it regardless).</summary>
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
        if (!full && !GateOwed(changed))
        {
            Log.Info("test: the gate did not run: nothing it builds changed (pass -full to run it anyway)");
            return;
        }
        await Gate.RunAsync(repo, ct).ConfigureAwait(false);
    }

    public static bool GateOwed(IEnumerable<string> changed) =>
        changed.Any(file => GateInputs.Any(root => file.Replace('\\', '/').StartsWith(root, StringComparison.Ordinal)));
}

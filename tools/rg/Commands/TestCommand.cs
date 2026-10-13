using RimGovernor.Tools.Common;

namespace RimGovernor.Tools.Commands;

/// <summary>
/// Runs the checks a change owes: gofmt on the changed files, go vet and staticcheck on the module, then
/// go test -short ./... (Go's test cache replays unchanged packages), plus the native contract probes build when its
/// inputs changed (Go: <c>cmd/test</c>).
/// <code>RimGovernor.cmd test [-base main] [-full]</code>
/// Run from anywhere inside the worktree. It diffs the working tree (committed, staged, unstaged and untracked)
/// against -base, so it is the edit/test loop's check as well as the pre-land one; the landing lane does not test.
/// -short skips the slow tests so the default run stays near 30 s; -full runs them, as the nightly does, even with
/// no changed Go files.
/// </summary>
internal static class TestCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var flags = Flags.Parse(args, new HashSet<string> { "full" }, new HashSet<string> { "base" });
        if (flags.HelpRequested)
        {
            Log.Info("usage: test [-base main] [-full]");
            Log.Info("  -base   revision to diff the working tree against (default main)");
            Log.Info("  -full   check the whole module including slow tests, even without changes; for the end of an epic");
            return 0;
        }
        var repo = Repo.Require(Directory.GetCurrentDirectory());
        var changed = await Affected.ChangedFilesAsync(repo, flags.String("base", "main"), ct).ConfigureAwait(false);
        await Affected.TestAsync(repo, changed, flags.Bool("full"), ct).ConfigureAwait(false);
        return 0;
    }
}

using System.Diagnostics;
using RimGovernor.Tools.Common;

namespace RimGovernor.Tools.Commands;

/// <summary>
/// Finds the files a change touched and the checks worth running for them: lint and go test on the Go module when Go
/// changed, the native contract probes build when its inputs did (Go: <c>internal/affected</c>). Also the
/// <c>affected</c> subcommand, which only reports what a change owes.
/// </summary>
internal static class Affected
{
    /// <summary>Roots whose files the native contract probes build compiles or links.</summary>
    private static readonly string[] ProbeInputs =
    [
        "integrations/rimgovernor-native/src",
        "contracts/tests",
        "contracts/generated/protobuf/csharp",
    ];

    /// <summary>Roots of the C# solutions; their gate is <c>task build</c> and <c>task test</c>, not this tool.</summary>
    private static readonly string[] CSharpRoots = ["brain/", "tools/rg/", "tools/launcher/"];

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
        Log.Info(GoChanged(changed) ? "go: lint and go test -short ./... (run: test)" : "go: nothing to test");
        Log.Info(ProbesChanged(changed) ? "probes: task probes:build (run: test)" : "probes: unaffected");
        Log.Info(CSharpChanged(changed) ? "csharp: task build and task test" : "csharp: unaffected");
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
    /// Runs the checks the changed files owe: the native contract probes build when a probe input changed, then lint
    /// and go test -short over the whole module when anything under go/ changed (go's own cache replays every
    /// package whose inputs are unchanged).
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
        var goDir = Path.Combine(repo, "go");
        if (ProbesChanged(changed))
        {
            Log.Info("probes: native contract probes build affected, running task probes:build");
            await Proc.RunAsync(repo, "task", ["probes:build"], ct).ConfigureAwait(false);
        }
        if (!full && !GoChanged(changed))
        {
            Log.Info("tests: no Go files changed, nothing to test");
            return;
        }
        await LintAsync(goDir, changed, ["./..."], ct).ConfigureAwait(false);
        Log.Info("tests: ./... (a package prints only when it finishes; silence is normal)");
        await TimedAsync("tests", () => GoTestAsync(goDir, full, ["./..."], ct)).ConfigureAwait(false);
    }

    public static bool ProbesChanged(IEnumerable<string> changed) =>
        changed.Any(file => ProbeInputs.Any(root => file.Replace('\\', '/').StartsWith(root + "/", StringComparison.Ordinal)));

    /// <summary>Whether a change touches anything under go/: Go sources, testdata, embedded inputs, go.mod.</summary>
    public static bool GoChanged(IEnumerable<string> changed) =>
        changed.Any(file => file.Replace('\\', '/').StartsWith("go/", StringComparison.Ordinal));

    public static bool CSharpChanged(IEnumerable<string> changed) =>
        changed.Any(file => CSharpRoots.Any(root => file.Replace('\\', '/').StartsWith(root, StringComparison.Ordinal)));

    /// <summary>Runs a stage and prints how long it took, so a quiet stage still ends with a visible line.</summary>
    private static async Task TimedAsync(string stage, Func<Task> body)
    {
        var started = Stopwatch.StartNew();
        var status = "failed";
        try
        {
            await body().ConfigureAwait(false);
            status = "ok";
        }
        finally
        {
            Log.Info($"{stage}: {status} ({Durations.Format(started.Elapsed)})");
        }
    }

    /// <summary>
    /// Runs the static gates <c>task go:build</c> applies to the whole module (gofmt, go vet, staticcheck) on the
    /// changed Go files and the affected packages, so a finding surfaces in the edit/test loop.
    /// </summary>
    private static async Task LintAsync(string goDir, IReadOnlyList<string> changed, string[] packages, CancellationToken ct)
    {
        var files = new List<string>();
        foreach (var file in changed)
        {
            var slashed = file.Replace('\\', '/');
            if (!slashed.EndsWith(".go", StringComparison.Ordinal) || !slashed.StartsWith("go/", StringComparison.Ordinal))
            {
                continue;
            }
            var rel = slashed["go/".Length..].Replace('/', Path.DirectorySeparatorChar);
            if (File.Exists(Path.Combine(goDir, rel)))
            {
                files.Add(rel);
            }
        }
        foreach (var batch in FileBatches(files))
        {
            var unformatted = (await Proc.OutputAsync(goDir, "gofmt", ["-l", .. batch], ct).ConfigureAwait(false)).Trim();
            if (unformatted.Length > 0)
            {
                throw new ToolException("gofmt -l: " + string.Join(' ', unformatted.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
            }
        }
        Log.Info("lint: go vet and staticcheck on " + string.Join(' ', packages) + " (silent when clean)");
        await TimedAsync(
                "lint",
                async () =>
                {
                    try
                    {
                        await Proc.RunAsync(goDir, "go", ["vet", .. packages], ct).ConfigureAwait(false);
                    }
                    catch (ToolException)
                    {
                        throw new ToolException("go vet: findings above");
                    }
                    try
                    {
                        await Proc.RunAsync(goDir, "go", ["tool", "staticcheck", .. packages], ct).ConfigureAwait(false);
                    }
                    catch (ToolException)
                    {
                        throw new ToolException("staticcheck: findings above");
                    }
                })
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs go test, rendering <c>-json</c> as text and warning about unmarked slow tests after the run. Under
    /// <paramref name="full"/> nothing is slow by design, so it streams plain go test.
    /// </summary>
    private static async Task GoTestAsync(string dir, bool full, string[] pkgs, CancellationToken ct)
    {
        if (full)
        {
            await Proc.RunAsync(dir, "go", ["test", .. pkgs], ct).ConfigureAwait(false);
            return;
        }
        var render = new SlowTestRenderer(Console.Out);
        try
        {
            await Proc.RunAsync(dir, "go", ["test", "-json", "-short", .. pkgs], ct, render.Line).ConfigureAwait(false);
        }
        finally
        {
            render.Warn();
        }
    }

    /// <summary>
    /// Leaves room for the executable, flags and terminating NUL below Windows' 32767 UTF-16-unit command line limit.
    /// Twice the UTF-8 byte length plus quotes and a separator bounds Windows quoting and UTF-16 encoding.
    /// </summary>
    private const int FileArgumentBudget = 16000;

    private static List<List<string>> FileBatches(IReadOnlyList<string> files)
    {
        var batches = new List<List<string>>();
        var current = new List<string>();
        var size = 0;
        foreach (var file in files)
        {
            var cost = (2 * System.Text.Encoding.UTF8.GetByteCount(file)) + 3;
            if (cost > FileArgumentBudget)
            {
                throw new ToolException($"file argument exceeds command-line budget: \"{file}\"");
            }
            if (size + cost > FileArgumentBudget)
            {
                batches.Add(current);
                current = [];
                size = 0;
            }
            current.Add(file);
            size += cost;
        }
        if (current.Count > 0)
        {
            batches.Add(current);
        }
        return batches;
    }
}

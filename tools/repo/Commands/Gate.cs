using System.Diagnostics;
using System.Text;
using RimGovernor.Tools.Common;

namespace RimGovernor.Tools.Workflow.Commands;

/// <summary>
/// The gate a change passes before it lands. Builds run one after another: RimGovernor.slnx (locked restore, warnings
/// as errors), the native contract probes against the brain contracts it just built, the real-Harmony hook set probe,
/// and the native mod when the game's assemblies are installed and its inputs changed. Then every test job runs at
/// once, each printed whole when it finishes. Output lands under <c>.rimgovernor/gate</c>; the solution builds into its
/// own artifacts root there, so it shares no bin, obj or restore file with an editor's builds of the same projects (#3346).
/// </summary>
internal static class Gate
{
    private const string DefaultRimWorldDir = @"C:\Program Files (x86)\Steam\steamapps\common\RimWorld";

    private const string DefaultHarmonyAssembly =
        @"C:\Program Files (x86)\Steam\steamapps\workshop\content\294100\2009463077\1.7\Assemblies\0Harmony.dll";

    /// <summary>The pure probes the gate runs; game-dependent probes run through native acceptance.</summary>
    private static readonly string[] Probes =
    [
        "native-roof-support", "native-designation-guards", "native-home-coverage", "native-construction-tier-gate",
        "native-speed-key-block", "native-clock-ceiling", "native-clock-hold-serial", "native-team-policy", "native-traffic-counts",
        "native-production-bill-settings", "native-observation-work",
        "native-threat-classifier", "native-acquisition-token", "native-ref-index", "native-proto-boundary",
    ];

    private sealed record Paths(string Repo)
    {
        public string Out => Path.Combine(Repo, ".rimgovernor", "gate");

        public string Artifacts => Path.Combine(Out, "artifacts");

        /// <summary>Where the artifacts layout puts a Release build of <paramref name="project"/>.</summary>
        public string Bin(string project) => Path.Combine(Artifacts, "bin", project, "release");

        public string Probes => Path.Combine(Out, "probes");

        public string HookSet => Path.Combine(Out, "hookset");

        public string Mod => Path.Combine(Out, "native", "mod");

        public string Manifest => Path.Combine(Mod, "RimGovernor", "native-manifest.json");

        public string RimWorldDir { get; } = Environment.GetEnvironmentVariable("RIMWORLD_DIR") is { Length: > 0 } dir ? dir : DefaultRimWorldDir;

        public string Harmony { get; } = Environment.GetEnvironmentVariable("HARMONY_ASSEMBLY") is { Length: > 0 } dll ? dll : DefaultHarmonyAssembly;
    }

    private sealed record Job(string Name, Func<CancellationToken, Task<string>> Run);

    public static async Task RunAsync(string repo, CancellationToken ct)
    {
        var p = new Paths(repo);
        Directory.CreateDirectory(p.Out);
        await BuildAsync(p, ct).ConfigureAwait(false);
        await TestAsync(p, ct).ConfigureAwait(false);
    }

    private static async Task BuildAsync(Paths p, CancellationToken ct)
    {
        await StepAsync(p.Repo, "build RimGovernor.slnx", ["build", "RimGovernor.slnx", .. Strict, .. ArtifactsOf(p)], ct).ConfigureAwait(false);
        var probes = Path.Combine(p.Repo, "contracts", "tests");
        await StepAsync(
            probes,
            "build native contract probes",
            ["build", "NativeContractProbes.csproj", .. Strict, $"-p:OutputPath={p.Probes}/bin/", $"-p:BaseIntermediateOutputPath={p.Probes}/obj/",
                $"-p:BrainBinDir={p.Bin("RimGovernor.Brain.Contracts")}"],
            ct).ConfigureAwait(false);
        if (!File.Exists(p.Harmony))
        {
            Log.Info($"build native-hook-set: unavailable (missing {p.Harmony})");
        }
        else
        {
            await StepAsync(
                probes,
                "build native-hook-set",
                ["build", "NativeHookSetProbe/NativeHookSetProbe.csproj", "-c", "Release", "-v", "minimal", "-nologo",
                    $"-p:HarmonyAssembly={p.Harmony}", $"-p:OutputPath={p.HookSet}/bin/", $"-p:BaseIntermediateOutputPath={p.HookSet}/obj/"],
                ct).ConfigureAwait(false);
        }
        await BuildNativeAsync(p, ct).ConfigureAwait(false);
    }

    private static string[] ArtifactsOf(Paths p) => ["-p:UseArtifactsOutput=true", $"-p:ArtifactsPath={p.Artifacts}"];

    private static string[] Strict => ["-c", "Release", "-v", "minimal", "-nologo", "-p:RestoreLockedMode=true", "-p:TreatWarningsAsErrors=true"];

    /// <summary>
    /// Packages the native mod when the game is installed, with the rg it just built: <c>native-build -if-current</c>
    /// keeps the last package when it records these sources, game inputs and fixtures.
    /// </summary>
    private static async Task BuildNativeAsync(Paths p, CancellationToken ct)
    {
        var game = Path.Combine(p.RimWorldDir, "Assembly-CSharp.dll");
        var missing = new[] { game, p.Harmony }.Where(f => !File.Exists(f)).ToList();
        if (missing.Count > 0)
        {
            Log.Info($"build native mod: unavailable (missing {string.Join(", ", missing)})");
            return;
        }
        await StepAsync(
            p.Repo,
            "build native mod",
            [Path.Combine(p.Bin("rg"), "rg.dll"), "native-build", "-if-current", "-out", p.Mod, "-rimworld", p.RimWorldDir, "-harmony", p.Harmony],
            ct).ConfigureAwait(false);
    }

    private static async Task TestAsync(Paths p, CancellationToken ct)
    {
        var probes = Path.Combine(p.Probes, "bin", "NativeContractProbes.dll");
        var hookset = Path.Combine(p.HookSet, "bin", "NativeHookSetProbe.dll");
        var jobs = new List<Job>
        {
            new("dotnet test RimGovernor.slnx", c => Dotnet(p.Repo, c, ["test", "RimGovernor.slnx", "-c", "Release", "--nologo", "--no-build", .. ArtifactsOf(p)])),
            new("native contract probes", async c =>
            {
                var output = new StringBuilder();
                foreach (var probe in Probes)
                {
                    output.Append(await Dotnet(p.Repo, c, probes, probe).ConfigureAwait(false));
                }
                return output.ToString();
            }),
            new("native-hook-set", c =>
                File.Exists(hookset) ? Dotnet(p.Repo, c, hookset) : Task.FromResult("unavailable (not built; Harmony missing)\n")),
            new("contract orphans", _ => Task.Run(() => Orphans(p))),
            new("banned-API baseline", _ => Task.Run(() => BannedApi(p))),
        };
        Log.Info($"test: {jobs.Count} jobs at once; each prints when it finishes");
        var failed = new List<string>();
        var console = new SemaphoreSlim(1, 1);
        await Task.WhenAll(jobs.Select(async job =>
        {
            var started = Stopwatch.StartNew();
            string output;
            string verdict;
            try
            {
                output = await job.Run(ct).ConfigureAwait(false);
                verdict = "PASS";
            }
            catch (ToolException e)
            {
                output = e.Message;
                verdict = "FAIL";
            }
            await console.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Log.Info($"---- {job.Name}: {verdict} ({Durations.Format(started.Elapsed)})");
                Log.Info(output.TrimEnd());
                if (verdict == "FAIL")
                {
                    failed.Add(job.Name);
                }
            }
            finally
            {
                console.Release();
            }
        })).ConfigureAwait(false);
        if (failed.Count > 0)
        {
            throw new ToolException($"failed: {string.Join(", ", failed)}");
        }
    }

    /// <summary>
    /// The orphan check (<see cref="ContractOrphans"/>) over the brain assemblies the solution built and the native
    /// mod's own, against its shrink-only baseline <c>brain/analyzers/baseline/orphans.txt</c>: an orphan not listed
    /// fails, and so does a listed line that is no longer an orphan (delete it). Without the game the native mod is not
    /// built and the game-reading brain projects are empty, so the wire and Policy checks have no evidence and say
    /// unavailable; the fixture contracts, whose readers are the acceptance cases, are checked either way.
    /// </summary>
    private static string Orphans(Paths p)
    {
        var brain = Directory.EnumerateDirectories(Path.Combine(p.Artifacts, "bin"), "RimGovernor.Brain.*")
            .Select(dir => Path.GetFileName(dir))
            .Where(name => name != "RimGovernor.Brain.Analyzers" && !name.EndsWith(".Tests", StringComparison.Ordinal))
            .Select(name => Path.Combine(p.Bin(name), name + ".dll"))
            .Where(File.Exists)
            .ToList();
        var found = new List<(string Key, string Reason)>();
        var counts = new List<string>();
        var fixtures = Path.Combine(p.Bin("RimGovernor.Fixtures.Contracts"), "RimGovernor.Fixtures.Contracts.dll");
        found.AddRange(ContractOrphans.Find(ContractOrphans.Fixtures, fixtures, brain, [], out var fixtureCount));
        counts.Add($"{ContractOrphans.Fixtures.Name} {fixtureCount}");
        var targets = new List<string> { ContractOrphans.Fixtures.Name };
        if (File.Exists(p.Manifest))
        {
            var native = Directory.EnumerateFiles(Path.Combine(p.Mod, "RimGovernor"), "RimGovernor.*.dll", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).StartsWith("RimGovernor.Brain.", StringComparison.Ordinal))
                .ToList();
            // The acceptance cases read some native replies by JSON key; any wire member whose JSON name they mention is read.
            var keys = ContractOrphans.LiteralKeys(brain.Where(f => Path.GetFileName(f).StartsWith("RimGovernor.Brain.Acceptance", StringComparison.Ordinal)));
            var contracts = Path.Combine(p.Bin("RimGovernor.Brain.Contracts"), "RimGovernor.Brain.Contracts.dll");
            var byName = ContractOrphans.WireNames(contracts).Where(n => keys.Contains(n.Value)).Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var (target, project) in new[] { (ContractOrphans.Wire, "RimGovernor.Brain.Contracts"), (ContractOrphans.PolicyRecords, "RimGovernor.Brain.Policy") })
            {
                var declaring = Path.Combine(p.Bin(project), project + ".dll");
                found.AddRange(ContractOrphans.Find(
                    target, declaring, brain.Where(f => f != declaring), native, out var checkedCount, target == ContractOrphans.Wire ? byName : null));
                counts.Add($"{target.Name} {checkedCount}");
                targets.Add(target.Name);
            }
        }
        else
        {
            counts.Add("Contracts and Policy unavailable (the native mod is not built)");
        }
        var baselinePath = Path.Combine(p.Repo, "brain", "analyzers", "baseline", "orphans.txt");
        var baseline = File.ReadAllLines(baselinePath)
            .Where(l => l.Length > 0 && targets.Contains(l[..l.IndexOf('|', StringComparison.Ordinal)]))
            .ToHashSet(StringComparer.Ordinal);
        var added = found.Where(f => !baseline.Contains(f.Key)).Select(f => $"{f.Key}: {f.Reason}").ToList();
        var stale = baseline.Except(found.Select(f => f.Key), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var problems = new List<string>();
        if (added.Count > 0)
        {
            problems.Add($"{added.Count} new orphans; delete each through every layer (never add a baseline line):\n  {string.Join("\n  ", added)}");
        }
        if (stale.Count > 0)
        {
            problems.Add($"{stale.Count} baseline lines are no longer orphans; delete them from {baselinePath}:\n  {string.Join("\n  ", stale)}");
        }
        if (problems.Count > 0)
        {
            throw new ToolException(string.Join("\n", problems));
        }
        return $"members checked: {string.Join(", ", counts)}; new orphans: none; baselined: {baseline.Count}\n";
    }

    /// <summary>
    /// The acceptance cases' banned-API baseline (<see cref="BannedApiBaseline"/>) against the compiler log the solution
    /// build wrote for <c>RimGovernor.Brain.Acceptance.Cases</c>.
    /// </summary>
    private static string BannedApi(Paths p)
    {
        const string project = "RimGovernor.Brain.Acceptance.Cases";
        var cases = Path.Combine(p.Repo, "brain", "src", project);
        var sarif = Path.Combine(p.Artifacts, "obj", project, "release", "banned-api.sarif");
        if (!File.Exists(sarif))
        {
            throw new ToolException($"missing {sarif}: the solution build writes it when it compiles the cases");
        }
        var problems = BannedApiBaseline.Check(cases, Path.Combine(cases, ".editorconfig"), sarif, out var baselined);
        if (problems.Count > 0)
        {
            throw new ToolException(string.Join("\n", problems));
        }
        return $"legacy files baselined: {baselined}; stale: none\n";
    }

    /// <summary>Runs dotnet captured and returns its output; a non-zero exit throws with that output.</summary>
    private static async Task<string> Dotnet(string dir, CancellationToken ct, params string[] args)
    {
        var result = await Proc.CaptureAsync(dir, "dotnet", args, ct).ConfigureAwait(false);
        var output = result.Stdout + result.Stderr;
        if (result.ExitCode != 0)
        {
            throw new ToolException($"{output.TrimEnd()}\nexit code {result.ExitCode}");
        }
        return output;
    }

    private static async Task StepAsync(string dir, string name, IReadOnlyList<string> args, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        Log.Info($"{name} ...");
        await Proc.RunAsync(dir, "dotnet", args, ct).ConfigureAwait(false);
        Log.Info($"{name}: done ({Durations.Format(started.Elapsed)})");
    }
}

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
        "native-speed-key-block", "native-clock-ceiling", "native-team-policy", "native-traffic-counts",
        "native-production-bill-settings", "native-observation-work", "native-reply-encoder",
        "native-threat-classifier", "native-acquisition-token", "native-ref-index",
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

        public string HostDll => Path.Combine(Mod, "RimGovernor", "Assemblies", "RimGovernor.Host.dll");

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
            new("native-proto-boundary", c =>
                File.Exists(p.HostDll) ? Dotnet(p.Repo, c, probes, "native-proto-boundary", p.HostDll)
                : File.Exists(p.Manifest) ? throw new ToolException($"the native build is staged but {p.HostDll} is missing")
                : Task.FromResult($"unavailable (missing {p.HostDll})\n")),
            new("native-hook-set", c =>
                File.Exists(hookset) ? Dotnet(p.Repo, c, hookset) : Task.FromResult("unavailable (not built; Harmony missing)\n")),
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

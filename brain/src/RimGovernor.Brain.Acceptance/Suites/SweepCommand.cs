using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Simulation.CombatDispatch;
using RimGovernor.Brain.Simulation.CombatMetrics;
using SimMetrics = RimGovernor.Brain.Simulation.CombatMetrics.Metrics;

namespace RimGovernor.Brain.Acceptance;

/// <summary>A combat-lab tactic variant: a named <see cref="CombatTuning"/> swept in the real game over the same seeds (Go <c>combatlab.Variant</c>).</summary>
public sealed record CombatVariant(string Name, CombatTuning Tuning);

/// <summary>
/// What the sweep needs from the combat lab's case package (Go <c>cases/combatlab</c>), which another slice owns: the tactic
/// variants, the shot dataset builder and where a recorded sweep lands in the source tree.
/// </summary>
public interface ICombatLab
{
    /// <summary>The ranged slice's tactic variants, the default (<see cref="SweepCommand.DefaultVariant"/>) first (Go <c>combatlab.Variants</c>).</summary>
    IReadOnlyList<CombatVariant> Variants();

    /// <summary>
    /// Writes the shot dataset of a sweep: for each seed the shot rows of the bundle directory <paramref name="caseDir"/>
    /// names, stamped with the seed. A seed without a shot file throws <see cref="AcceptanceException"/>: the dataset
    /// covers every seed (Go <c>combatlab.BuildShotDataset</c>).
    /// </summary>
    void BuildShotDataset(Stream output, IReadOnlyList<int> seeds, Func<int, string> caseDir);

    /// <summary>
    /// The file a recorded sweep of <paramref name="fixture"/> lands in: the committed baseline, or with a non-empty
    /// <paramref name="variant"/> the calibration variant file. Throws <see cref="AcceptanceException"/> when the source tree
    /// cannot be found (Go resolved <c>BaselinesDir</c> / <c>CalibrationDir</c> against the nearest go.mod).
    /// </summary>
    string RecordPath(string fixture, string variant);
}

/// <summary>The sweep subcommand's resolved configuration (Go <c>sweepOptions</c>).</summary>
public sealed record SweepOptions
{
    public IReadOnlyList<string> Fixtures { get; init; } = [];

    public string Root { get; init; } = "";

    public string Output { get; init; } = "";

    public string GameId { get; init; } = "rimgovernor-trial";

    public IReadOnlyList<int> Seeds { get; init; } = [];

    public int Workers { get; init; } = 1;

    public string Against { get; init; } = "";

    public bool Restage { get; init; }

    public bool Record { get; init; }

    public string Shots { get; init; } = "";

    /// <summary>Names a tactic variant; <see cref="TuningEnv"/> is its <see cref="PolicyFunctions.CombatTuningEnv"/> value, set on every child.</summary>
    public string Variant { get; init; } = "";

    public string TuningEnv { get; init; } = "";

    public TimeSpan CaseTimeout { get; init; }
}

/// <summary>The sweep files' source-generated JSON (indented like Go's <c>MarshalIndent</c>; field names come from the records' attributes).</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Sweep))]
[JsonSerializable(typeof(Comparison))]
[JsonSerializable(typeof(SimMetrics), TypeInfoPropertyName = "SimMetrics")]
internal sealed partial class SweepJsonContext : JsonSerializerContext
{
}

/// <summary>
/// <c>acceptance sweep</c> (Go <c>cmd/acceptance/sweep.go</c>): runs <c>combatlab/metrics-&lt;fixture&gt;</c> once per seed
/// from one staged checkpoint (seed s reseeds the RNG after the load, <c>-run-seed</c>) on one or two private clones of
/// <c>-root</c>, and writes <c>&lt;output&gt;/&lt;fixture&gt;/sweep.json</c> (per-seed rows plus summary). The report prints
/// deaths and downs first; <c>-against</c> pairs the seeds with an earlier sweep and the exit code is 1 on a survival
/// regression.
/// </summary>
public static class SweepCommand
{
    /// <summary>The seed count a baseline sweep records (seeds 1..N): Go <c>combatlab.BaselineSeeds</c>.</summary>
    public const int BaselineSeeds = 20;

    /// <summary>The variant whose tuning is the shipped one, whose real sweep is the committed baseline (Go <c>calibrate.DefaultVariant</c>).</summary>
    public const string DefaultVariant = "default";

    /// <summary>The aggregate a metrics case writes beside its result.json (Go <c>combatlab.MetricsFile</c>).</summary>
    public const string MetricsFile = "combat_metrics.json";

    private const string CasePrefix = "combatlab/metrics-";

    /// <summary>The usage lines for the command's help text.</summary>
    public const string Usage = """
          acceptance sweep <fixture>... -root <dir> -output <dir> [-n 20 | -seeds 1,2,5 -workers 1|2 -against <sweep.json|dir> -restage -record -variant <name> -shots <file> -game <id> -case-timeout <d>]
            runs combatlab/metrics-<fixture> once per seed from one staged checkpoint (seed s reseeds the RNG after the load, -run-seed),
            on 1 or 2 private clones of -root, and writes <output>/<fixture>/sweep.json (per-seed rows plus summary) with the report printed
            deaths and downs first; -against pairs the seeds with an earlier sweep.json (or <dir>/<fixture>/sweep.json) and exits 1 on a survival regression
        """;

    /// <summary>Runs the subcommand with the arguments after <c>sweep</c>; returns the process exit code (2 refused, 1 a failed seed or a regression).</summary>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        CaseRegistry registry,
        ICombatLab lab,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(lab);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        SweepOptions opts;
        try
        {
            opts = Parse(args, lab);
        }
        catch (AcceptanceException e)
        {
            await stderr.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 2;
        }

        // Taken before any game of the sweep boots, so the count is the box's peers.
        var load = CombatDispatcher.LoadNote();
        var selected = new List<AcceptanceCase>();
        foreach (var f in opts.Fixtures)
        {
            var c = registry.Lookup(CasePrefix + f);
            if (c is null)
            {
                await stderr.WriteLineAsync($"unknown fixture \"{f}\" (no case {CasePrefix}{f})").ConfigureAwait(false);
                return 2;
            }

            selected.Add(c);
        }

        var pre = await PreflightPass.RunAsync(
            selected,
            new RunOptions { Root = opts.Root, Output = opts.Output, GameId = opts.GameId, NoHeal = true },
            stderr,
            cancellationToken).ConfigureAwait(false);
        if (!pre.Ok)
        {
            return 2;
        }

        var roots = new string[opts.Workers];
        for (var i = 0; i < roots.Length; i++)
        {
            var dest = Path.Combine(opts.Output, "workers", (i + 1).ToString(CultureInfo.InvariantCulture));
            try
            {
                _ = HeadlessProfile.IsolatedRoot(opts.Root, dest);
            }
            catch (AcceptanceException e)
            {
                await stderr.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"worker {i + 1} root: {e.Message}")).ConfigureAwait(false);
                return 2;
            }

            roots[i] = dest;
        }

        var exit = 0;
        try
        {
            foreach (var c in selected)
            {
                var fixture = c.Name[CasePrefix.Length..];
                var code = await SweepFixtureAsync(c, fixture, opts, lab, load, roots, stdout, stderr, cancellationToken).ConfigureAwait(false);
                exit = Math.Max(exit, code);
            }
        }
        finally
        {
            foreach (var r in roots)
            {
                using var stopCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                try
                {
                    _ = await Game.StopGameAsync(r, opts.GameId, stopCts.Token).ConfigureAwait(false);
                }
                catch (AcceptanceException e)
                {
                    await stderr.WriteLineAsync($"stop {r}: {e.Message}").ConfigureAwait(false);
                }
                catch (OperationCanceledException e)
                {
                    await stderr.WriteLineAsync($"stop {r}: {e.Message}").ConfigureAwait(false);
                }
            }
        }

        return exit;
    }

    /// <summary>Resolves the sweep subcommand's flags (Go <c>parseSweep</c>); throws <see cref="UsageException"/> on a refused invocation.</summary>
    public static SweepOptions Parse(IReadOnlyList<string> args, ICombatLab lab)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(lab);
        var fs = new FlagSet("sweep");
        var n = fs.Int("n", BaselineSeeds);
        var record = fs.Bool("record", false);
        var seeds = fs.String("seeds", "");
        var root = fs.String("root", "");
        var output = fs.String("output", "");
        var game = fs.String("game", "rimgovernor-trial");
        var workers = fs.Int("workers", 1);
        var against = fs.String("against", "");
        var shots = fs.String("shots", "");
        var variant = fs.String("variant", "");
        var restage = fs.Bool("restage", false);
        var caseTimeout = fs.Duration("case-timeout", TimeSpan.Zero);
        var fixtures = fs.ParseAfterPositionals(args);
        if (fs.Args.Count > 0)
        {
            throw new UsageException($"fixture names must precede the flags: [{string.Join(' ', fs.Args)}]");
        }

        if (fixtures.Count == 0)
        {
            throw new UsageException("sweep needs at least one fixture (lab-choke, ...)");
        }

        if (root.Value.Length == 0 || output.Value.Length == 0)
        {
            throw new UsageException("-root and -output are required");
        }

        if (!Path.IsPathFullyQualified(root.Value))
        {
            throw new UsageException("-root must be absolute");
        }

        var shotsPath = "";
        if (shots.Value.Length > 0)
        {
            if (fixtures.Count != 1)
            {
                throw new UsageException("-shots writes one fixture's dataset: name one fixture");
            }

            shotsPath = Path.GetFullPath(shots.Value);
        }

        var tuningEnv = variant.Value.Length > 0 ? VariantEnv(lab, variant.Value) : "";
        var list = SweepSeeds(n.Value, seeds.Value);
        if (record.Value && (seeds.Value.Length > 0 || n.Value != BaselineSeeds))
        {
            throw new UsageException(string.Create(CultureInfo.InvariantCulture, $"-record writes the {BaselineSeeds}-seed baseline: omit -n and -seeds"));
        }

        if (workers.Value < 1 || workers.Value > CombatDispatcher.MaxWorkers)
        {
            throw new UsageException(string.Create(CultureInfo.InvariantCulture, $"-workers must be 1 to {CombatDispatcher.MaxWorkers}"));
        }

        return new SweepOptions
        {
            Fixtures = fixtures,
            Root = root.Value,
            Output = Path.GetFullPath(output.Value),
            GameId = game.Value,
            Seeds = list,
            Workers = workers.Value,
            Against = against.Value.Length > 0 ? Path.GetFullPath(against.Value) : "",
            Restage = restage.Value,
            Record = record.Value,
            Shots = shotsPath,
            Variant = variant.Value,
            TuningEnv = tuningEnv,
            CaseTimeout = caseTimeout.Value,
        };
    }

    /// <summary>The CombatTuningEnv value of a named non-default variant. The default variant is the committed baseline: sweep it without -variant.</summary>
    public static string VariantEnv(ICombatLab lab, string name)
    {
        ArgumentNullException.ThrowIfNull(lab);
        var variants = lab.Variants();
        foreach (var v in variants)
        {
            if (!string.Equals(v.Name, name, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(name, DefaultVariant, StringComparison.Ordinal))
            {
                throw new UsageException($"-variant {name} is the baseline: sweep without -variant");
            }

            var data = TuningJson(v.Tuning).ToJsonString();
            try
            {
                _ = PolicyFunctions.ParseCombatTuning(data);
            }
            catch (DomainException e)
            {
                throw new UsageException(e.Message, e);
            }

            return data;
        }

        throw new UsageException($"unknown -variant \"{name}\" (have {string.Join(", ", variants.Select(v => v.Name))})");
    }

    /// <summary>The tuning as the JSON object the service reads (the Go field names; every field, so the child does not depend on the default).</summary>
    internal static JsonObject TuningJson(CombatTuning t) => new()
    {
        ["DoorCloseRange"] = t.DoorCloseRange,
        ["DoorOpenRange"] = t.DoorOpenRange,
        ["DoorGunners"] = t.DoorGunners,
        ["FlankMinDefenders"] = t.FlankMinDefenders,
        ["FlankSize"] = t.FlankSize,
        ["FlankLateral"] = t.FlankLateral,
        ["GroupGap"] = t.GroupGap,
        ["KiteLeadRange"] = t.KiteLeadRange,
        ["KiteSpeedRatio"] = t.KiteSpeedRatio,
        ["ScatterCells"] = t.ScatterCells,
        ["DoseMargin"] = t.DoseMargin,
        ["RushRange"] = t.RushRange,
    };

    /// <summary>The explicit list or 1..n; seed 0 means no reseed and is refused (Go <c>sweepSeeds</c>).</summary>
    public static IReadOnlyList<int> SweepSeeds(int n, string list)
    {
        var seeds = new List<int>();
        if (list.Length > 0)
        {
            foreach (var f in list.Split(','))
            {
                if (!int.TryParse(f.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
                {
                    throw new UsageException($"-seeds: parsing \"{f.Trim()}\": invalid syntax");
                }

                seeds.Add(s);
            }
        }
        else
        {
            for (var s = 1; s <= n; s++)
            {
                seeds.Add(s);
            }
        }

        if (seeds.Count == 0)
        {
            throw new UsageException("no seeds: -n must be at least 1");
        }

        foreach (var s in seeds)
        {
            if (s <= 0)
            {
                throw new UsageException(string.Create(CultureInfo.InvariantCulture, $"seed {s}: seeds are positive (0 is no reseed)"));
            }
        }

        return seeds;
    }

    private static async Task<int> SweepFixtureAsync(
        AcceptanceCase c,
        string fixture,
        SweepOptions opts,
        ICombatLab lab,
        string load,
        string[] roots,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken ct)
    {
        var dir = Path.Combine(opts.Output, fixture);
        try
        {
            await StageForSweepAsync(c, opts, roots, dir, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException)
        {
            await stderr.WriteLineAsync($"{fixture}: stage: {e.Message}").ConfigureAwait(false);
            return 1;
        }

        // A failed seed keeps what it did read (the digest and draws), as Go's dispatch kept the value beside the error.
        var partial = new ConcurrentDictionary<int, SeedRow>();
        DispatchReport<SeedRow> report;
        string? dispatchError = null;
        try
        {
            report = await CombatDispatcher.Dispatch<SeedRow>(
                opts.Seeds,
                opts.Workers,
                load,
                async (worker, seed, token) =>
                {
                    await stderr.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"[worker {worker + 1}] {fixture} seed {seed}")).ConfigureAwait(false);
                    var (row, error) = await RunSeedAsync(c, opts, roots[worker], dir, worker, seed, token).ConfigureAwait(false);
                    if (error is not null)
                    {
                        partial[seed] = row;
                        await stderr.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"[worker {worker + 1}] {fixture} seed {seed}: {error.Message}")).ConfigureAwait(false);
                        throw error;
                    }

                    return row;
                },
                ct).ConfigureAwait(false);
        }
        catch (DispatchException<SeedRow> e)
        {
            report = e.Report;
            dispatchError = e.Message;
        }

        var rows = new List<SeedRow>(report.Runs.Count);
        foreach (var r in report.Runs)
        {
            var row = r.Value ?? (partial.TryGetValue(r.Seed, out var kept) ? kept : new SeedRow());
            rows.Add(row with { Seed = r.Seed, Worker = r.Worker + 1, Error = r.Error });
        }

        var result = Sweep.NewSweep(fixture, report.Workers, report.Load, report.WallMs, rows);
        var path = Path.Combine(dir, "sweep.json");
        try
        {
            WriteJson(path, result, SweepJsonContext.Default.Sweep);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await stderr.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 1;
        }

        await stdout.WriteAsync(result.Report()).ConfigureAwait(false);
        await stdout.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"  wall {result.WallMs} ms; written {path}")).ConfigureAwait(false);
        var code = 0;
        if (dispatchError is not null)
        {
            await stdout.WriteLineAsync($"FAIL\t{fixture}\t{dispatchError}").ConfigureAwait(false);
            code = 1;
        }

        if (opts.Shots.Length > 0)
        {
            try
            {
                WriteShotDataset(lab, c, dir, opts.Seeds, opts.Shots);
            }
            catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException)
            {
                await stdout.WriteLineAsync($"FAIL\t{fixture}\tshots not written: {e.Message}").ConfigureAwait(false);
                return 1;
            }

            await stdout.WriteLineAsync($"  shot dataset {opts.Shots}").ConfigureAwait(false);
        }

        if (opts.Record)
        {
            try
            {
                RecordSweep(lab, result, opts.Variant);
            }
            catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException)
            {
                await stdout.WriteLineAsync($"FAIL\t{fixture}\tnot recorded: {e.Message}").ConfigureAwait(false);
                return 1;
            }

            await stdout.WriteLineAsync(opts.Variant.Length > 0
                ? $"  recorded variant {fixture}/{opts.Variant}"
                : $"  recorded baseline {fixture}").ConfigureAwait(false);
        }

        if (opts.Against.Length > 0)
        {
            Comparison cmp;
            try
            {
                cmp = CompareAgainst(opts.Against, result);
            }
            catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                await stdout.WriteLineAsync($"FAIL\t{fixture}\t{e.Message}").ConfigureAwait(false);
                return 1;
            }

            await stdout.WriteAsync(cmp.Report()).ConfigureAwait(false);
            try
            {
                WriteJson(Path.Combine(dir, "comparison.json"), cmp, SweepJsonContext.Default.Comparison);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await stderr.WriteLineAsync(e.Message).ConfigureAwait(false);
            }

            if (cmp.Regressed())
            {
                code = 1;
            }
        }

        return code;
    }

    /// <summary>
    /// Writes a clean sweep over the fixture's committed baseline, or with a variant over its calibration variant file, in
    /// the source tree (Go <c>recordSweep</c>).
    /// </summary>
    internal static void RecordSweep(ICombatLab lab, Sweep s, string variant)
    {
        var defects = s.Defects();
        if (defects.Count > 0)
        {
            throw new AcceptanceException($"sweep is not a baseline: {string.Join("; ", defects)}");
        }

        WriteJson(lab.RecordPath(s.Fixture, variant), s, SweepJsonContext.Default.Sweep);
    }

    /// <summary>Loads the baseline sweep (a file, or &lt;dir&gt;/&lt;fixture&gt;/sweep.json, or the baselines directory's &lt;fixture&gt;.json) and compares the candidate against it.</summary>
    internal static Comparison CompareAgainst(string against, Sweep candidate)
    {
        var path = against;
        if (Directory.Exists(against))
        {
            path = Path.Combine(against, candidate.Fixture, "sweep.json");
            if (!File.Exists(path))
            {
                path = Path.Combine(against, candidate.Fixture + ".json");
            }
        }

        var text = File.ReadAllText(path);
        Sweep baseline;
        try
        {
            baseline = Sweep.ParseSweep(text);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        {
            throw new AcceptanceException($"{path}: {e.Message}", e);
        }

        return Comparison.Compare(baseline, candidate);
    }

    internal static void WriteJson<T>(string path, T value, JsonTypeInfo<T> info)
    {
        var data = JsonSerializer.Serialize(value, info);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, data + "\n");
    }

    /// <summary>
    /// Stages the fixture once, on worker 1, and gives every worker root its own copy of the bundle (through the shared
    /// root, which keeps the bundle for the next sweep). Every seed then resumes that one checkpoint.
    /// </summary>
    private static async Task StageForSweepAsync(AcceptanceCase c, SweepOptions opts, string[] roots, string dir, CancellationToken ct)
    {
        if (!opts.Restage)
        {
            SuiteStages.CarryStages(opts.Root, roots[0], c.Name);
        }

        var argv = new List<string> { "-through", "staged", "-checkpoint-every", "0" };
        if (opts.Restage)
        {
            argv.Add("-restage");
        }

        await RunChildAsync(c, opts, roots[0], Path.Combine(dir, "stage"), argv, ct).ConfigureAwait(false);
        _ = SuiteStages.PublishStages(opts.Root, roots[0], c.Name);
        foreach (var r in roots.Skip(1))
        {
            SuiteStages.CarryStages(opts.Root, r, c.Name);
        }
    }

    /// <summary>Runs <c>acceptance run</c> for the case on a worker root into <paramref name="output"/>; throws when it cannot start or exits non-zero.</summary>
    private static async Task RunChildAsync(AcceptanceCase c, SweepOptions opts, string root, string output, IReadOnlyList<string> extra, CancellationToken ct)
    {
        var tail = new List<string> { "run", c.Name, "-root", root, "-output", output, "-game", opts.GameId, "-no-doctor", "-no-series" };
        if (opts.CaseTimeout > TimeSpan.Zero)
        {
            tail.AddRange(["-timeout", GoDuration.Format(opts.CaseTimeout)]);
        }

        tail.AddRange(extra);
        Directory.CreateDirectory(output);
        var logPath = output + ".log";
        var env = new Dictionary<string, string>(StringComparer.Ordinal) { [Game.KeepGameEnv] = "1" };
        if (opts.TuningEnv.Length > 0)
        {
            env[PolicyFunctions.CombatTuningEnv] = opts.TuningEnv;
        }

        var (self, _) = SelfInvocation.Resolve();
        var result = await ChildProcess.RunAsync(self, SelfInvocation.AcceptanceArgv(tail), env, logPath, ct).ConfigureAwait(false);
        if (result.Code != 0)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{c.Name}: exit status {result.Code} (see {logPath})"));
        }
    }

    /// <summary>
    /// Concatenates the seeds' shot tap rows (combat_shots.jsonl in each seed's bundle), each stamped with its seed, into one
    /// file. A seed without a shot file is an error: the dataset must cover every seed.
    /// </summary>
    private static void WriteShotDataset(ICombatLab lab, AcceptanceCase c, string dir, IReadOnlyList<int> seeds, string path)
    {
        using var buffer = new MemoryStream();
        lab.BuildShotDataset(buffer, seeds, seed => SeedCaseDir(c, dir, seed));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, buffer.ToArray());
    }

    private static string SeedCaseDir(AcceptanceCase c, string dir, int seed) =>
        new RunOptions { Output = Path.Combine(dir, string.Create(CultureInfo.InvariantCulture, $"seed-{seed}")) }.CaseOutput(c);

    /// <summary>Runs one seed on a worker root and reads the seed's row from its bundle: the metrics and the staging digest and reseed draws it reported.</summary>
    private static async Task<(SeedRow Row, AcceptanceException? Error)> RunSeedAsync(
        AcceptanceCase c,
        SweepOptions opts,
        string root,
        string dir,
        int worker,
        int seed,
        CancellationToken ct)
    {
        var row = new SeedRow { Seed = seed, Worker = worker + 1 };
        var output = Path.Combine(dir, string.Create(CultureInfo.InvariantCulture, $"seed-{seed}"));
        AcceptanceException? runError = null;
        try
        {
            await RunChildAsync(c, opts, root, output, ["-checkpoint-every", "0", "-run-seed", seed.ToString(CultureInfo.InvariantCulture)], ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException)
        {
            runError = e as AcceptanceException ?? new AcceptanceException(e.Message, e);
        }

        var caseDir = new RunOptions { Output = output }.CaseOutput(c);
        try
        {
            if (JsonNode.Parse(File.ReadAllText(Path.Combine(caseDir, "result.json"))) is JsonObject result)
            {
                var draws = result["run_seed_draws"] is JsonArray a
                    ? a.Select(n => n is JsonValue v && v.TryGetValue<int>(out var d) ? d : 0).ToList()
                    : null;
                row = row with
                {
                    StagedDigest = result["staged_digest"] is JsonValue sv && sv.TryGetValue<string>(out var digest) ? digest : null,
                    RunSeedDraws = draws,
                };
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // No readable result.json: the row carries what the metrics say.
        }

        if (runError is not null)
        {
            return (row, runError);
        }

        try
        {
            var data = await File.ReadAllTextAsync(Path.Combine(caseDir, MetricsFile), ct).ConfigureAwait(false);
            var m = JsonSerializer.Deserialize(data, SweepJsonContext.Default.SimMetrics);
            return (row with { Metrics = m }, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (row, new AcceptanceException(e.Message, e));
        }
        catch (JsonException e)
        {
            return (row, new AcceptanceException($"{MetricsFile}: {e.Message}", e));
        }
    }
}

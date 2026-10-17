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

/// <summary>A combat-lab tactic variant: a named <see cref="CombatTuning"/> swept in the real game over the same seeds.</summary>
public sealed record CombatVariant(string Name, CombatTuning Tuning);

/// <summary>The sweep subcommand's resolved configuration.</summary>
public sealed record SweepOptions
{
    public IReadOnlyList<string> Fixtures { get; init; } = [];

    public string Root { get; init; } = "";

    public string Output { get; init; } = "";

    public string GameId { get; init; } = "rimgovernor-trial";

    public IReadOnlyList<int> Seeds { get; init; } = [];

    public int Workers { get; init; } = 1;

    public string Against { get; init; } = "";

    public bool Record { get; init; }

    public string Shots { get; init; } = "";

    /// <summary>Names a tactic variant; <see cref="TuningEnv"/> is its <see cref="CombatTuningRules.CombatTuningEnv"/> value, launched into every worker's game.</summary>
    public string Variant { get; init; } = "";

    public string TuningEnv { get; init; } = "";

    /// <summary>One seed's hang guard, from the game open to its bundle.</summary>
    public TimeSpan SeedTimeout { get; init; } = SweepCommand.DefaultSeedTimeout;
}

/// <summary>The sweep files' source-generated JSON (indented; field names come from the records' attributes).</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Sweep))]
[JsonSerializable(typeof(Comparison))]
[JsonSerializable(typeof(SimMetrics), TypeInfoPropertyName = "SimMetrics")]
internal sealed partial class SweepJsonContext : JsonSerializerContext
{
}

/// <summary>
/// <c>acceptance sweep</c>: the combat lab's own driver, outside the case runner. For each fixture it runs every seed in
/// this process on one or two private clones of <c>-root</c> (<see cref="SweepSeed"/>: stage through the stage spec,
/// reseed with the seed, serve, score) and writes <c>&lt;output&gt;/&lt;fixture&gt;/sweep.json</c> (per-seed rows plus
/// summary). The report prints deaths and downs first; <c>-against</c> pairs the seeds with an earlier sweep and the exit
/// code is 1 on a survival regression. <c>hunting-grid-&lt;cell&gt;</c> collects the hunting qualification instead.
/// </summary>
public static class SweepCommand
{
    /// <summary>The seed count a baseline sweep records (seeds 1..N).</summary>
    public const int BaselineSeeds = 20;

    /// <summary>The variant whose tuning is the shipped one, whose real sweep is the committed baseline.</summary>
    public const string DefaultVariant = "default";

    /// <summary>The baselines directory relative to the repository root.</summary>
    public const string BaselinesDir = "brain/src/RimGovernor.Brain.Acceptance/Sweep/baselines";

    /// <summary>
    /// The calibration directory relative to the repository root: variants/ holds the real-game sweep files
    /// <c>&lt;fixture&gt;/&lt;variant&gt;.json</c> of the non-default variants, shots/ the committed shot datasets.
    /// </summary>
    public const string CalibrationDir = "brain/src/RimGovernor.Brain.Acceptance/Sweep/calibration";

    /// <summary>One seed's default hang guard: a cold launch and lab start plus the served fight and the reads after.</summary>
    public static readonly TimeSpan DefaultSeedTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The usage lines for the command's help text.</summary>
    public const string Usage = """
          acceptance sweep <fixture>... -root <dir> -output <dir> [-n 20 | -seeds 1,2,5 -workers 1|2 -against <sweep.json|dir> -record -variant <name> -shots <file> -game <id> -timeout <d>]
            stages <fixture> afresh for each seed through the stage spec, reseeds the RNG with the seed and serves the defense planner,
            on 1 or 2 private clones of -root, and writes <output>/<fixture>/sweep.json (per-seed rows plus summary) with the report printed
            deaths and downs first; -against pairs the seeds with an earlier sweep.json (or <dir>/<fixture>/sweep.json) and exits 1 on a survival regression
          acceptance sweep hunting-grid-<cell> -root <dir> -output <dir> [-seeds a,b,c -workers 1|2]
            the cell's one predictor request, then one native hunt per seed (hunting-prediction-qualification.md)
          acceptance sweep -hunting-report <output-dir> <report.json>
        """;

    internal const string HuntingPrefix = "hunting-grid-";

    /// <summary>
    /// Runs the subcommand with the arguments after <c>sweep</c>; returns the process exit code (2 refused, 1 a failed seed
    /// or a regression). <paramref name="predictor"/> runs a hunting cell's predictor request; null refuses hunting cells.
    /// </summary>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        HuntingPredictor? predictor,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        if (args.Count > 0 && args[0] == "-hunting-report")
        {
            return SweepHunting.HuntingReport([.. args.Skip(1)], stdout, stderr);
        }

        SweepOptions opts;
        try
        {
            opts = Parse(args);
        }
        catch (AcceptanceException e)
        {
            await stderr.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 2;
        }

        var ops = new List<string>();
        foreach (var f in opts.Fixtures)
        {
            if (f.StartsWith(HuntingPrefix, StringComparison.Ordinal))
            {
                if (opts.Record || opts.Against.Length != 0 || opts.Variant.Length != 0 || opts.Shots.Length != 0)
                {
                    await stderr.WriteLineAsync("hunting qualification uses retained trial reports, not combat tactic baselines").ConfigureAwait(false);
                    return 2;
                }

                if (predictor is null)
                {
                    await stderr.WriteLineAsync("sweep: the host wired no hunting predictor").ConfigureAwait(false);
                    return 2;
                }

                ops.AddRange(SweepHunting.HuntingOps);
            }
            else
            {
                ops.AddRange(SweepSeed.Ops);
            }
        }

        // Taken before any game of the sweep boots, so the count is the box's peers.
        var load = CombatDispatcher.LoadNote();
        var pre = await PreflightPass.RunAsync(
            opts.Fixtures,
            [.. ops.Distinct(StringComparer.Ordinal)],
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
            foreach (var f in opts.Fixtures)
            {
                if (f.StartsWith(HuntingPrefix, StringComparison.Ordinal))
                {
                    var huntingExit = await SweepHunting.SweepHuntingAsync(f[HuntingPrefix.Length..], predictor!, opts, load, roots, stdout, stderr, cancellationToken).ConfigureAwait(false);
                    exit = Math.Max(exit, huntingExit);
                    if (huntingExit != 0)
                    {
                        break;
                    }

                    continue;
                }

                var code = await SweepFixtureAsync(f, opts, load, roots, stdout, stderr, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Resolves the sweep subcommand's flags; throws <see cref="UsageException"/> on a refused invocation.</summary>
    public static SweepOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
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
        var timeout = fs.Duration("timeout", DefaultSeedTimeout);
        var fixtures = fs.ParseAfterPositionals(args);
        if (fs.Args.Count > 0)
        {
            throw new UsageException($"fixture names must precede the flags: [{string.Join(' ', fs.Args)}]");
        }

        if (fixtures.Count == 0)
        {
            throw new UsageException("sweep needs at least one fixture (lab-choke, ...)");
        }

        foreach (var f in fixtures)
        {
            var known = f.StartsWith(HuntingPrefix, StringComparison.Ordinal)
                ? HuntingQualification.Cells.Contains(f[HuntingPrefix.Length..], StringComparer.Ordinal)
                : CombatFixtures.Names.Contains(f, StringComparer.Ordinal);
            if (!known)
            {
                throw new UsageException($"unknown fixture \"{f}\" (have {string.Join(", ", CombatFixtures.Names)} and {HuntingPrefix}<cell>)");
            }
        }

        if (root.Value.Length == 0 || output.Value.Length == 0)
        {
            throw new UsageException("-root and -output are required");
        }

        if (!Path.IsPathFullyQualified(root.Value))
        {
            throw new UsageException("-root must be absolute");
        }

        if (timeout.Value <= TimeSpan.Zero)
        {
            throw new UsageException("-timeout must be positive");
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

        var tuningEnv = variant.Value.Length > 0 ? VariantEnv(variant.Value) : "";
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
            Record = record.Value,
            Shots = shotsPath,
            Variant = variant.Value,
            TuningEnv = tuningEnv,
            SeedTimeout = timeout.Value,
        };
    }

    /// <summary>
    /// The ranged slice's tactic variants, the default first: the two tuning knobs the ranged fallback and raid grouping
    /// read, each moved both ways.
    /// </summary>
    public static IReadOnlyList<CombatVariant> Variants()
    {
        var shipped = CombatTuningRules.DefaultCombatTuning();
        return
        [
            new CombatVariant(DefaultVariant, shipped),
            new CombatVariant("scatter-short", shipped with { ScatterCells = 8 }),
            new CombatVariant("scatter-long", shipped with { ScatterCells = 45 }),
            new CombatVariant("group-gap-tight", shipped with { GroupGap = 3 }),
            new CombatVariant("group-gap-wide", shipped with { GroupGap = 20 }),
        ];
    }

    /// <summary>The CombatTuningEnv value of a named non-default variant. The default variant is the committed baseline: sweep it without -variant.</summary>
    public static string VariantEnv(string name)
    {
        var variants = Variants();
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
                _ = CombatTuningRules.ParseCombatTuning(data);
            }
            catch (DomainException e)
            {
                throw new UsageException(e.Message, e);
            }

            return data;
        }

        throw new UsageException($"unknown -variant \"{name}\" (have {string.Join(", ", variants.Select(v => v.Name))})");
    }

    /// <summary>
    /// The file a recorded sweep of <paramref name="fixture"/> lands in under <paramref name="repo"/>: the committed
    /// baseline, or with a non-empty <paramref name="variant"/> the calibration variant file.
    /// </summary>
    public static string RecordPath(string repo, string fixture, string variant) =>
        variant.Length == 0
            ? Path.Combine(repo, BaselinesDir, fixture + ".json")
            : Path.Combine(repo, CalibrationDir, "variants", fixture, variant + ".json");

    /// <summary>The tuning as the JSON object the service reads (the Go field names; every field, so the game does not depend on the default).</summary>
    internal static JsonObject TuningJson(CombatTuning t) => new()
    {
        { "DoorCloseRange", t.DoorCloseRange },
        { "DoorOpenRange", t.DoorOpenRange },
        { "DoorGunners", t.DoorGunners },
        { "FlankMinDefenders", t.FlankMinDefenders },
        { "FlankSize", t.FlankSize },
        { "FlankLateral", t.FlankLateral },
        { "GroupGap", t.GroupGap },
        { "KiteLeadRange", t.KiteLeadRange },
        { "KiteSpeedRatio", t.KiteSpeedRatio },
        { "ScatterCells", t.ScatterCells },
        { "DoseMargin", t.DoseMargin },
        { "RushRange", t.RushRange },
    };

    /// <summary>The explicit list or 1..n; seed 0 means no reseed and is refused.</summary>
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

    internal static string SeedDir(string dir, int seed) => Path.Combine(dir, string.Create(CultureInfo.InvariantCulture, $"seed-{seed}"));

    private static async Task<int> SweepFixtureAsync(
        string fixture,
        SweepOptions opts,
        string load,
        string[] roots,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken ct)
    {
        var dir = Path.Combine(opts.Output, fixture);

        // A failed seed keeps what it did read (the digest and draws) beside the error.
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
                    var (row, error) = await SweepSeed.RunAsync(opts, roots[worker], fixture, worker, seed, SeedDir(dir, seed), token).ConfigureAwait(false);
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
                await WriteShotDatasetAsync(dir, opts.Seeds, opts.Shots, ct).ConfigureAwait(false);
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
                RecordSweep(result, opts.Variant);
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
    /// the source tree (resolved against the nearest .git above the working directory).
    /// </summary>
    internal static void RecordSweep(Sweep s, string variant)
    {
        var defects = s.Defects();
        if (defects.Count > 0)
        {
            throw new AcceptanceException($"sweep is not a baseline: {string.Join("; ", defects)}");
        }

        var repo = NativeSourceInputs.FindRepo(Directory.GetCurrentDirectory())
            ?? throw new AcceptanceException("cannot find the source tree (no .git above the working directory) to record the sweep in");
        WriteJson(RecordPath(repo, s.Fixture, variant), s, SweepJsonContext.Default.Sweep);
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
    /// Concatenates the seeds' shot tap rows (combat_shots.jsonl in each seed's bundle), each stamped with its seed, into one
    /// file. A seed without a shot file is an error: the dataset must cover every seed.
    /// </summary>
    private static async Task WriteShotDatasetAsync(string dir, IReadOnlyList<int> seeds, string path, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await CombatLabShots.BuildShotDatasetAsync(buffer, seeds, seed => SeedDir(dir, seed), ct).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, buffer.ToArray(), ct).ConfigureAwait(false);
    }
}

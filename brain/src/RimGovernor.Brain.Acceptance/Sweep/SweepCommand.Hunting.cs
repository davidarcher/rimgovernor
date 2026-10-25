using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Simulation.CombatDispatch;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;

namespace RimGovernor.Brain.Acceptance;

/// <summary>What a hunting cell's predictor request starts from: the scene observed natively and where its files go.</summary>
public sealed record HuntingPredictionInput(string Cell, EncounterRequest Observed, string PredictorMods, string Output);

/// <summary>A hunting cell's predictor request: the 16-sample record, the cold-start request and the cancelled request.</summary>
public sealed record HuntingPredictionRun(HuntingCellPrediction Record, EncounterPrediction Cold, EncounterPrediction Cancelled);

/// <summary>
/// Runs a hunting cell's one predictor request with its runtime measurements. The host wires it (tools/rg, over Brain.Host's
/// local encounter predictor and the RealCombat worker), so this assembly does not reference the brain host.
/// </summary>
public delegate Task<HuntingPredictionRun> HuntingPredictor(HuntingPredictionInput input, CancellationToken cancellationToken);

public static class SweepHunting
{
    /// <summary>A cell seed's native trial reply, kept whether or not it resolved.</summary>
    public const string NativeTrialFile = "native_trial.json";

    /// <summary>A cell seed's <see cref="HuntingTrial"/>.</summary>
    public const string HuntingTrialFile = "hunting_trial.json";

    /// <summary>A cell's <see cref="HuntingCellPrediction"/>.</summary>
    public const string HuntingPredictionFile = "hunting_cell_prediction.json";

    private const string HuntingTool = "test/hunting_prediction_trial";

    private static readonly JsonSerializerOptions HuntingJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static IReadOnlyList<string> HuntingOps => [DebugGame.LabStartTool, HuntingTool];

    // A cell's one predictor request on worker 1 (the scene observed natively under seed 1, which is never collected),
    // then one native encounter per seed on the shared dispatcher, which stops queued trials after the first failure.
    internal static async Task<int> SweepHuntingAsync(string cell, HuntingPredictor predictor, SweepOptions opts, string load, string[] roots,
        TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var (prey, hunters, index) = HuntingCell(cell);
        var directory = Path.Combine(opts.Output, SweepCommand.HuntingPrefix + cell);
        var start = new LabStart { Colonists = Math.Max(2, hunters) };
        await stderr.WriteLineAsync($"hunting {cell} prediction").ConfigureAwait(false);
        var predicted = await SweepSeed.WithGameAsync(
            opts,
            roots[0],
            Path.Combine(directory, "prediction"),
            $"The {cell} cell's one {HuntingQualification.PredictionSamples}-sample hypothetical prediction with runtime measurements.",
            start,
            [],
            (session, cfg, report, token) => PredictAsync(session.Harness, cfg.Output, report, predictor, cell, prey, hunters, token),
            ct).ConfigureAwait(false);
        if (predicted is not null)
        {
            await stderr.WriteLineAsync($"hunting {cell} prediction: {predicted.Message}").ConfigureAwait(false);
            return 1;
        }

        try
        {
            await CombatDispatcher.Dispatch<int>(opts.Seeds, opts.Workers, load, async (worker, seed, token) =>
            {
                await stderr.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"hunting {cell} seed {seed} worker {worker + 1}")).ConfigureAwait(false);
                var error = await SweepSeed.WithGameAsync(
                    opts,
                    roots[worker],
                    SweepCommand.SeedDir(directory, seed),
                    $"One native hunt of {cell} with revenge enabled; never a statistical pass alone.",
                    start,
                    [],
                    (session, cfg, report, t) => TrialAsync(session.Harness, cfg.Output, report, cell, prey, hunters, checked(index * 100000 + seed), t),
                    token).ConfigureAwait(false);
                return error is null ? seed : throw error;
            }, ct, stopOnFailure: true).ConfigureAwait(false);
            await stdout.WriteLineAsync("retained hunting trials: " + directory).ConfigureAwait(false);
            return 0;
        }
        catch (DispatchException<int> e)
        {
            await stderr.WriteLineAsync(e.Message + "; failed and unresolved trial artifacts remain in " + directory).ConfigureAwait(false);
            return 1;
        }
    }

    // The cell's prey, hunter count and 1-based index in the grid (the native seed's high digits).
    private static ((string Prey, string Animal, int Count) Prey, int Hunters, int Index) HuntingCell(string cell)
    {
        for (var p = 0; p < HuntingQualification.PreyKinds.Length; p++)
        {
            for (var h = 0; h < HuntingQualification.HunterCounts.Length; h++)
            {
                var prey = HuntingQualification.PreyKinds[p];
                var hunters = HuntingQualification.HunterCounts[h];
                if (HuntingQualification.CellId(prey.Prey, hunters) == cell)
                {
                    return (prey, hunters, p * HuntingQualification.HunterCounts.Length + h + 1);
                }
            }
        }

        throw new UsageException($"unknown hunting cell \"{cell}\"");
    }

    private static Task<JsonObject> NativeAsync(Harness h, (string Prey, string Animal, int Count) prey, int hunters, int seed, CancellationToken ct) =>
        h.CallAsync("hunting-native-trial", HuntingTool, new JsonObject
        {
            { "seed", seed }, { "stratum", "probe" }, { "horizon", 6000 }, { "animal", prey.Animal },
            { "preyCount", prey.Count }, { "hunters", hunters },
        }, ct);

    // The native trial reply as the collection reads it; the bundle keeps the reply itself.
    private static HuntingNativeTrial ReadNative(JsonObject native)
    {
        try
        {
            return native.Deserialize<HuntingNativeTrial>(HuntingJson) ?? throw new AcceptanceException("missing native trial");
        }
        catch (JsonException e)
        {
            throw new AcceptanceException($"{HuntingTool}: the reply does not decode: {e.Message}", e);
        }
    }

    private static async Task TrialAsync(Harness h, string output, Report report, string cell, (string Prey, string Animal, int Count) prey, int hunters, int nativeSeed, CancellationToken ct)
    {
        var raw = await NativeAsync(h, prey, hunters, nativeSeed, ct).ConfigureAwait(false);

        // Failed and unresolved native outcomes are retained.
        await File.WriteAllTextAsync(Path.Combine(output, NativeTrialFile), raw.ToJsonString(HuntingJson), ct).ConfigureAwait(false);
        var native = ReadNative(raw);
        var request = native.Request ?? throw new AcceptanceException("missing native request");
        var observed = native.Observed ?? throw new AcceptanceException("missing native observed outcome");
        var trial = new HuntingTrial(cell + ":" + nativeSeed.ToString(CultureInfo.InvariantCulture), HuntingQualification.Revision, cell, native.NativeRunId ?? "", nativeSeed,
            native.NativeBuild ?? "", native.NativeMods ?? "", native.InitialStateMatched,
            native.RevengeFactor > 0, request, observed)
        { RetaliationObserved = native.Retaliation.Any(state => (state ?? "").EndsWith(":Manhunter", StringComparison.Ordinal)) };
        await File.WriteAllTextAsync(Path.Combine(output, HuntingTrialFile), JsonSerializer.Serialize(trial, HuntingJson), ct).ConfigureAwait(false);
        report["native_seed"] = nativeSeed;
        if (!trial.InitialStateMatched || !trial.RevengeEnabled)
        {
            throw new AcceptanceException("Native initial state or revenge setting did not match; stop collection.");
        }
    }

    private static async Task PredictAsync(Harness h, string output, Report report, HuntingPredictor predictor, string cell, (string Prey, string Animal, int Count) prey, int hunters, CancellationToken ct)
    {
        // The scene is observed once natively (seed 1, not collected); only its request and its hunt read are used.
        var native = ReadNative(await NativeAsync(h, prey, hunters, 1, ct).ConfigureAwait(false));
        var observed = native.Request ?? throw new AcceptanceException("missing native request");
        var run = await predictor(new HuntingPredictionInput(cell, observed, native.PredictorMods ?? "", output), ct).ConfigureAwait(false);
        var (analytic, refusal) = Analytic(native.HuntRead);
        var record = run.Record with { Analytic = analytic, AnalyticRefusal = refusal };
        await File.WriteAllTextAsync(Path.Combine(output, HuntingPredictionFile), JsonSerializer.Serialize(record, HuntingJson), ct).ConfigureAwait(false);
        report["cancelled_prediction"] = JsonSerializer.SerializeToNode(run.Cancelled, HuntingJson);
        report["qualified"] = false;
        if (run.Cold.Status != EncounterStatus.Evidence || record.Prediction.Status != EncounterStatus.Evidence)
        {
            throw new AcceptanceException("Prediction failed: cold=" + run.Cold.Reason + "; warm=" + record.Prediction.Reason);
        }

        if (run.Cancelled.Status != EncounterStatus.Cancelled || record.CancellationMilliseconds is null or > 2000)
        {
            throw new AcceptanceException("Prediction cancellation/cleanup gate failed.");
        }

        if (record.ColdStartMilliseconds is null or > 15000 || record.WarmMilliseconds is null or > 20000)
        {
            throw new AcceptanceException("Prediction cold/warm runtime gate failed; diagnostic timings retained.");
        }
    }

    // The served estimate of the scene: every hunter of the read at every prey, as the cell sends them.
    private static (HuntEstimate? Estimate, string Refusal) Analytic(string? huntRead)
    {
        if (string.IsNullOrEmpty(huntRead)) return (null, "hunt_read_missing");
        var read = JsonSerializer.Deserialize(huntRead, OrdersJsonContext.Default.HuntActivityRead) ?? throw new AcceptanceException("the hunt read does not decode");
        var roster = new HuntRoster(EquatableArray.From(read.Hunters.Select(a => a.Pawn)), EquatableArray.From(read.Prey.Select(a => a.Pawn)));
        return HuntEstimator.Estimate(read, roster);
    }

    internal static int HuntingReport(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length != 2)
        {
            stderr.WriteLine("usage: acceptance sweep -hunting-report <retained-output-directory> <report.json>");
            return 2;
        }

        var root = Path.GetFullPath(args[0]);
        var trials = new List<HuntingTrial>();
        var predictions = new List<HuntingCellPrediction>();
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, HuntingTrialFile, SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            trials.Add(JsonSerializer.Deserialize<HuntingTrial>(File.ReadAllText(file), HuntingJson) ?? throw new InvalidDataException(file));
        }

        foreach (var file in Directory.EnumerateFiles(root, HuntingPredictionFile, SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            predictions.Add(JsonSerializer.Deserialize<HuntingCellPrediction>(File.ReadAllText(file), HuntingJson) ?? throw new InvalidDataException(file));
        }

        // A seed directory of a cell that holds no trial is a trial that never resolved.
        foreach (var cellDir in Directory.EnumerateDirectories(root, SweepCommand.HuntingPrefix + "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            foreach (var seedDir in Directory.EnumerateDirectories(cellDir, "seed-*").Order(StringComparer.Ordinal))
            {
                if (!File.Exists(Path.Combine(seedDir, HuntingTrialFile)))
                {
                    missing.Add(seedDir);
                }
            }
        }

        var score = HuntingQualification.Score(predictions, trials);
        if (missing.Count != 0)
        {
            score = score with { Failures = [.. score.Failures, "missing_trial_artifacts:" + missing.Count.ToString(CultureInfo.InvariantCulture)] };
        }

        var comparison = HuntingQualification.Compare(predictions, trials);
        var report = new
        {
            score,
            comparison,
            missingTrials = missing,
            predictions,
            trials,
            qualificationAccepted = false,
            review = "Accuracy alone does not pass runtime, reconstruction or scope review. No provider self-certification.",
        };
        File.WriteAllText(Path.GetFullPath(args[1]), JsonSerializer.Serialize(report, HuntingJson));
        foreach (var c in comparison)
        {
            stdout.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{c.Cell}: harm sim={c.SimulatorHarm:0.00} analytic={c.AnalyticHarm:0.00} native={c.NativeHarm:0.00}; downed sim={c.SimulatorDowned:0.00} analytic={c.AnalyticDowned:0.00} native={c.NativeDowned:0.00}; ticks sim={c.SimulatorMedianTicks} analytic={c.AnalyticTicks:0} native={c.NativeMedianTicks}; death analytic={c.AnalyticDeath:0.000}; native trials={c.NativeTrials}{(c.AnalyticRefusal.Length == 0 ? "" : "; refused " + c.AnalyticRefusal)}"));
        }

        stdout.WriteLine(string.Create(CultureInfo.InvariantCulture, $"hunting trials={score.Trials}, cells={score.Cells.Count}, safe={score.SafeObservations}, false-safe={score.FalseSafe}, inversions={score.RankingInversions}; accuracy={score.AccuracyPassed}"));
        return score.AccuracyPassed ? 0 : 1;
    }
}

/// <summary>The hunting fixture's native trial reply (Web JSON) as the collection reads it.</summary>
internal sealed record HuntingNativeTrial
{
    public EncounterRequest? Request { get; init; }

    public EncounterSample? Observed { get; init; }

    public string? NativeRunId { get; init; }

    public string? NativeBuild { get; init; }

    public string? NativeMods { get; init; }

    public string? PredictorMods { get; init; }

    /// <summary>The served hunt read of the scene before a shot, as wire JSON (<c>OrdersJsonContext</c>).</summary>
    public string? HuntRead { get; init; }

    public bool InitialStateMatched { get; init; }

    public double RevengeFactor { get; init; }

    /// <summary>The prey's mental states over the trial, "pawn:state".</summary>
    public IReadOnlyList<string?> Retaliation { get; set; } = [];
}

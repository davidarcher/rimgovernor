using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Simulation.CombatDispatch;

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

public static partial class SweepCommand
{
    /// <summary>A cell seed's native trial reply, kept whether or not it resolved.</summary>
    public const string NativeTrialFile = "native_trial.json";

    /// <summary>A cell seed's <see cref="HuntingTrial"/>.</summary>
    public const string HuntingTrialFile = "hunting_trial.json";

    /// <summary>A cell's <see cref="HuntingCellPrediction"/>.</summary>
    public const string HuntingPredictionFile = "hunting_cell_prediction.json";

    private const string HuntingTool = "test/hunting_prediction_trial";

    private static readonly JsonSerializerOptions HuntingJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static IReadOnlyList<string> HuntingOps => [DebugGame.LabStartTool, HuntingTool];

    // A cell's one predictor request on worker 1 (the scene observed natively under seed 1, which is never collected),
    // then one native encounter per seed on the shared dispatcher, which stops queued trials after the first failure.
    private static async Task<int> SweepHuntingAsync(string cell, HuntingPredictor predictor, SweepOptions opts, string load, string[] roots,
        TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var (prey, hunters, index) = HuntingCell(cell);
        var directory = Path.Combine(opts.Output, HuntingPrefix + cell);
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
                    SeedDir(directory, seed),
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
            ["seed"] = seed, ["stratum"] = "probe", ["horizon"] = 6000, ["animal"] = prey.Animal,
            ["preyCount"] = prey.Count, ["hunters"] = hunters,
        }, ct);

    private static async Task TrialAsync(Harness h, string output, Report report, string cell, (string Prey, string Animal, int Count) prey, int hunters, int nativeSeed, CancellationToken ct)
    {
        var native = await NativeAsync(h, prey, hunters, nativeSeed, ct).ConfigureAwait(false);

        // Failed and unresolved native outcomes are retained.
        await File.WriteAllTextAsync(Path.Combine(output, NativeTrialFile), native.ToJsonString(HuntingJson), ct).ConfigureAwait(false);
        var request = native["request"].Deserialize<EncounterRequest>(HuntingJson) ?? throw new AcceptanceException("missing native request");
        var observed = native["observed"].Deserialize<EncounterSample>(HuntingJson) ?? throw new AcceptanceException("missing native observed outcome");
        var trial = new HuntingTrial(cell + ":" + nativeSeed.ToString(CultureInfo.InvariantCulture), HuntingQualification.Revision, cell, native["nativeRunId"].AsString(), nativeSeed,
            native["nativeBuild"].AsString(), native["nativeMods"].AsString(), native["initialStateMatched"].AsBool() == true,
            native["revengeFactor"].NumberOrZero() > 0, request, observed)
        { RetaliationObserved = native["retaliation"].AsSlice().Any(state => state.AsString().EndsWith(":Manhunter", StringComparison.Ordinal)) };
        await File.WriteAllTextAsync(Path.Combine(output, HuntingTrialFile), JsonSerializer.Serialize(trial, HuntingJson), ct).ConfigureAwait(false);
        report["native_seed"] = nativeSeed;
        if (!trial.InitialStateMatched || !trial.RevengeEnabled)
        {
            throw new AcceptanceException("Native initial state or revenge setting did not match; stop collection.");
        }
    }

    private static async Task PredictAsync(Harness h, string output, Report report, HuntingPredictor predictor, string cell, (string Prey, string Animal, int Count) prey, int hunters, CancellationToken ct)
    {
        // The scene is observed once natively (seed 1, not collected); only its request is used.
        var native = await NativeAsync(h, prey, hunters, 1, ct).ConfigureAwait(false);
        var observed = native["request"].Deserialize<EncounterRequest>(HuntingJson) ?? throw new AcceptanceException("missing native request");
        var run = await predictor(new HuntingPredictionInput(cell, observed, native["predictorMods"].AsString(), output), ct).ConfigureAwait(false);
        var record = run.Record;
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

    private static int HuntingReport(string[] args, TextWriter stdout, TextWriter stderr)
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
        foreach (var cellDir in Directory.EnumerateDirectories(root, HuntingPrefix + "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
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

        var report = new
        {
            score,
            missingTrials = missing,
            predictions,
            trials,
            qualificationAccepted = false,
            review = "Accuracy alone does not pass runtime, reconstruction or scope review. No provider self-certification.",
        };
        File.WriteAllText(Path.GetFullPath(args[1]), JsonSerializer.Serialize(report, HuntingJson));
        stdout.WriteLine(string.Create(CultureInfo.InvariantCulture, $"hunting trials={score.Trials}, cells={score.Cells.Count}, safe={score.SafeObservations}, false-safe={score.FalseSafe}, inversions={score.RankingInversions}; accuracy={score.AccuracyPassed}"));
        return score.AccuracyPassed ? 0 : 1;
    }
}

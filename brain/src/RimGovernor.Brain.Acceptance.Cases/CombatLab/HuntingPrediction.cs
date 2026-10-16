using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Host;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>Independent native observation and hypothetical prediction in the existing CombatLab artifact lifecycle.
/// Per (prey, hunter-set) cell: <c>hunting-grid-&lt;cell&gt;</c> is one native encounter per run seed and
/// <c>hunting-grid-predict-&lt;cell&gt;</c> is the cell's single predictor request.</summary>
internal static class CombatLabHuntingPrediction
{
    private const string Tool = "test/hunting_prediction_trial";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int PredictionSeedBase = 1000000;

    internal static IEnumerable<AcceptanceCase> Cases() => HuntingQualification.PreyKinds.SelectMany(prey =>
        HuntingQualification.HunterCounts.SelectMany(hunters =>
        {
            var cell = HuntingQualification.CellId(prey.Prey, hunters);
            var index = Array.IndexOf(HuntingQualification.PreyKinds, prey) * HuntingQualification.HunterCounts.Length + Array.IndexOf(HuntingQualification.HunterCounts, hunters) + 1;
            return new[]
            {
                Case("combatlab/hunting-grid-" + cell, hunters, "One native hunt of " + cell + " per run seed with revenge enabled; never a statistical pass alone.",
                    (session, cancellation) => NativeAsync(session, cell, prey, hunters, index, cancellation)),
                Case("combatlab/hunting-grid-predict-" + cell, hunters, "The cell's one 16-sample hypothetical prediction with runtime measurements.",
                    (session, cancellation) => PredictAsync(session, cell, prey, hunters, cancellation)),
            };
        }));

    private static AcceptanceCase Case(string name, int hunters, string scope, Func<ISession, CancellationToken, Task> run) => new()
    {
        Name = name,
        Scope = scope + " Native simulation cannot be established by unit tests or worker replay.",
        Start = new LabStart { Colonists = Math.Max(2, hunters) },
        RequiredOps = [DebugGame.LabStartTool, Tool],
        Crew = new Crew { Size = Math.Max(2, hunters), Ordinary = true },
        Reason = "The disposable fixture stages exactly the cell's hunters.",
        QuietWorld = false, // Quiet-world acceleration can suppress wild animals; revenge must execute normally.
        NoCheckpoint = true,
        Budget = TimeSpan.FromMinutes(5),
        Run = run,
    };

    private static Task<JsonObject> Native(ISession session, (string Prey, string Animal, int Count) prey, int hunters, int seed, CancellationToken cancellation) =>
        session.Harness.CallAsync("hunting-native-trial", Tool, new JsonObject
        {
            ["seed"] = seed, ["stratum"] = "probe", ["horizon"] = 6000, ["animal"] = prey.Animal,
            ["preyCount"] = prey.Count, ["hunters"] = hunters,
        }, cancellation);

    private static async Task NativeAsync(ISession session, string cell, (string Prey, string Animal, int Count) prey, int hunters, int index, CancellationToken cancellation)
    {
        var runSeed = session.RunSeed == 0 ? 1 : session.RunSeed;
        var nativeSeed = checked(index * 100000 + runSeed);
        var native = await Native(session, prey, hunters, nativeSeed, cancellation).ConfigureAwait(false);
        session.Report["native_trial"] = native.DeepClone(); // Retain failures/unresolved native outcomes.
        var request = native["request"]?.Deserialize<EncounterRequest>(Json) ?? throw new AcceptanceException("missing native request");
        var observed = native["observed"]?.Deserialize<EncounterSample>(Json) ?? throw new AcceptanceException("missing native observed outcome");
        var trial = new HuntingTrial(cell + ":" + nativeSeed, HuntingQualification.Revision, cell, native["nativeRunId"].AsString(), nativeSeed,
            native["nativeBuild"].AsString(), native["nativeMods"].AsString(), native["initialStateMatched"].AsBool() == true,
            native["revengeFactor"].AsNumberOrZero() > 0, request, observed)
        { RetaliationObserved = native["retaliation"].AsSlice().Any(state => state.AsString().EndsWith(":Manhunter", StringComparison.Ordinal)) };
        session.Report["hunting_trial"] = JsonSerializer.SerializeToNode(trial, Json);
        if (!trial.InitialStateMatched || !trial.RevengeEnabled) throw new AcceptanceException("Native initial state or revenge setting did not match; stop collection.");
    }

    private static async Task PredictAsync(ISession session, string cell, (string Prey, string Animal, int Count) prey, int hunters, CancellationToken cancellation)
    {
        var repo = NativeSourceInputs.FindRepo(Directory.GetCurrentDirectory()) ?? throw new AcceptanceException("repository root unavailable");
        var worker = Path.Combine(repo, "integrations", "rimgovernor-realcombat", "src", "RealCombat.Host", "bin", "Release", "RimGovernor.RealCombat.Host.dll");
        var game = Path.Combine(repo, ".rimgovernor", "native-rimworld");
        if (!File.Exists(worker)) throw new AcceptanceException("Build the existing RealCombat.Host Release worker before this case.");
        // The scene is observed once natively (seed 1, not collected); only its request is used.
        var native = await Native(session, prey, hunters, 1, cancellation).ConfigureAwait(false);
        var observedRequest = native["request"]?.Deserialize<EncounterRequest>(Json) ?? throw new AcceptanceException("missing native request");
        var samples = Enumerable.Range(1, HuntingQualification.PredictionSamples).Select(n => PredictionSeedBase + n).ToArray();
        var request = observedRequest with { Seeds = samples, SamplingReason = "hunt group-versus-single decision" };
        var coldRequest = observedRequest with { Seeds = [samples[0]], SamplingReason = "cold start" };
        var settings = new LocalEncounterOptions("dotnet", worker, game, session.Config.Output,
            request.Identity.GameBuild, request.Identity.Mods, native["predictorMods"].AsString(), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(2), HuntingQualification.PredictionSamples, 6000, 100, 8, 1048576);
        await using var predictor = new LocalEncounterPredictor(settings);
        var consumer = new EncounterPredictionConsumer(predictor, TimeSpan.FromSeconds(95));
        var cold = await consumer.PredictAsync(coldRequest, () => coldRequest, cancellation).ConfigureAwait(false);
        var warm = await consumer.PredictAsync(request, () => request, cancellation).ConfigureAwait(false);
        using var cancel = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        var pending = predictor.PredictAsync(request, cancel.Token);
        cancel.Cancel();
        var cancelled = await pending.ConfigureAwait(false);
        var cancellationMs = watch.Elapsed.TotalMilliseconds;
        var record = new HuntingCellPrediction(cell, request, warm, cold.ColdStartMilliseconds, warm.RequestMilliseconds, cancellationMs);
        session.Report["hunting_cell_prediction"] = JsonSerializer.SerializeToNode(record, Json);
        session.Report["cancelled_prediction"] = JsonSerializer.SerializeToNode(cancelled, Json);
        session.Report["qualified"] = false;
        if (cold.Status != EncounterStatus.Evidence || warm.Status != EncounterStatus.Evidence)
            throw new AcceptanceException("Prediction failed: cold=" + cold.Reason + "; warm=" + warm.Reason);
        if (cancelled.Status != EncounterStatus.Cancelled || cancellationMs > 2000)
            throw new AcceptanceException("Prediction cancellation/cleanup gate failed.");
        if (cold.ColdStartMilliseconds is null or > 15000 || warm.RequestMilliseconds is null or > 20000)
            throw new AcceptanceException("Prediction cold/warm runtime gate failed; diagnostic timings retained.");
    }
}

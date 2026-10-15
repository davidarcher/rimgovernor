using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Host;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>Independent native observation and hypothetical prediction in the existing CombatLab artifact lifecycle.</summary>
internal static class CombatLabHuntingPrediction
{
    private const string Tool = "test/hunting_prediction_trial";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static IEnumerable<AcceptanceCase> Cases() => HuntingQualification.RequiredStrata.Select(stratum => new AcceptanceCase
    {
        Name = "combatlab/hunting-prediction-" + stratum,
        Scope = "Observe real wild-prey revenge, native Hunt duration and health in the live game; compare with independently seeded hypothetical predictions. " +
            "One seed is a qualification pilot, never a statistical pass. Native simulation cannot be established by unit tests or worker replay.",
        Start = new LabStart { Colonists = 2 },
        RequiredOps = [DebugGame.LabStartTool, Tool],
        Crew = new Crew { Size = 2, Ordinary = true },
        Reason = "The disposable fixture stages exactly one hunter, or two for the departure stratum.",
        QuietWorld = false, // Quiet-world acceleration can suppress wild animals; revenge must execute normally.
        NoCheckpoint = true,
        Budget = TimeSpan.FromMinutes(5),
        Run = (session, cancellation) => RunAsync(session, stratum, cancellation),
    });

    private static async Task RunAsync(ISession session, string stratum, CancellationToken cancellation)
    {
        var repo = NativeSourceInputs.FindRepo(Directory.GetCurrentDirectory()) ?? throw new AcceptanceException("repository root unavailable");
        var worker = Path.Combine(repo, "integrations", "rimgovernor-realcombat", "src", "RealCombat.Host", "bin", "Release", "RimGovernor.RealCombat.Host.dll");
        var game = Path.Combine(repo, ".rimgovernor", "native-rimworld");
        if (!File.Exists(worker)) throw new AcceptanceException("Build the existing RealCombat.Host Release worker before this case.");
        var runSeed = session.RunSeed == 0 ? 1 : session.RunSeed;
        var nativeSeed = runSeed == 1 ? 1 : checked((Array.IndexOf(HuntingQualification.RequiredStrata, stratum) + 1) * 100000 + runSeed);
        var watch = Stopwatch.StartNew();
        var native = await session.Harness.CallAsync("hunting-native-trial", Tool,
            new JsonObject { ["seed"] = nativeSeed, ["stratum"] = stratum, ["horizon"] = 6000 }, cancellation).ConfigureAwait(false);
        var nativeMs = watch.Elapsed.TotalMilliseconds;
        session.Report["native_trial"] = native.DeepClone(); // Retain failures/unresolved native outcomes before worker execution.
        var request = native["request"]?.Deserialize<EncounterRequest>(Json) ?? throw new AcceptanceException("missing native request");
        var observed = native["observed"]?.Deserialize<EncounterSample>(Json) ?? throw new AcceptanceException("missing native observed outcome");
        // Pilot is deliberately one predictive seed. Broad collection uses the same case with the frozen protocol,
        // not identical-seed replays passed off as independent live observations.
        var settings = new LocalEncounterOptions("dotnet", worker, game, session.Config.Output,
            request.Identity.GameBuild, request.Identity.Mods, native["predictorMods"].AsString(), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(2), 1, 6000, 100, 8, 1048576);
        await using var predictor = new LocalEncounterPredictor(settings);
        var consumer = new EncounterPredictionConsumer(predictor, TimeSpan.FromSeconds(95));
        var cold = await consumer.PredictAsync(request, () => request, cancellation).ConfigureAwait(false);
        var warm = await consumer.PredictAsync(request, () => request, cancellation).ConfigureAwait(false);
        using var cancel = new CancellationTokenSource();
        watch.Restart();
        var pending = predictor.PredictAsync(request, cancel.Token);
        cancel.Cancel();
        var cancelled = await pending.ConfigureAwait(false);
        var cancellationMs = watch.Elapsed.TotalMilliseconds;
        session.Report["cancelled_prediction"] = JsonSerializer.SerializeToNode(cancelled, Json);
        session.Report["cancellation_ms"] = cancellationMs;
        var trial = new HuntingTrial(stratum + ":" + nativeSeed, HuntingQualification.Revision, stratum, runSeed >= 10000, "distance-comparison", stratum,
            native["nativeRunId"].AsString(), nativeSeed, native["nativeBuild"].AsString(), native["nativeMods"].AsString(),
            native["initialStateMatched"].AsBool() == true, native["revengeFactor"].AsNumberOrZero() > 0, request, warm, observed) { RetaliationObserved = native["retaliation"].AsSlice().Any(state => state.AsString().EndsWith(":Manhunter", StringComparison.Ordinal)) };
        session.Report["hunting_trial"] = JsonSerializer.SerializeToNode(trial, Json);
        session.Report["qualification"] = JsonSerializer.SerializeToNode(HuntingQualification.Score([trial]), Json);
        session.Report["cold_prediction"] = JsonSerializer.SerializeToNode(cold, Json);
        session.Report["native_ms"] = nativeMs;
        session.Report["qualified"] = false;
        if (cold.Status != EncounterStatus.Evidence || warm.Status != EncounterStatus.Evidence)
            throw new AcceptanceException("Prediction pilot failed: cold=" + cold.Reason + "; warm=" + warm.Reason);
        if (cancelled.Status != EncounterStatus.Cancelled || cancellationMs > 2000)
            throw new AcceptanceException("Prediction cancellation/cleanup gate failed.");
        if (cold.ColdStartMilliseconds is null or > 15000 || warm.RequestMilliseconds is null or > 1000)
            throw new AcceptanceException("Prediction cold/warm runtime gate failed; diagnostic timings retained.");
        if (!warm.Samples.Any(s => HuntingQualification.SeriousHarm(request, s)) && HuntingQualification.SeriousHarm(request, observed))
            throw new AcceptanceException("Observed false-safe: stop qualification collection and investigate.");
    }
}

using System.Diagnostics;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Host;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The hunting sweep's predictor request (<see cref="HuntingPredictor"/>): the local encounter predictor over the
/// Release RealCombat worker, one cold-start request, the cell's 16-sample request and a cancelled request.
/// </summary>
public static class CombatLabHuntingPredictor
{
    private const int PredictionSeedBase = 1000000;

    /// <summary>Runs the cell's predictor request; throws <see cref="AcceptanceException"/> when the worker is not built.</summary>
    public static async Task<HuntingPredictionRun> PredictAsync(HuntingPredictionInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var repo = NativeSourceInputs.FindRepo(Directory.GetCurrentDirectory()) ?? throw new AcceptanceException("repository root unavailable");
        var worker = Path.Combine(repo, "integrations", "rimgovernor-realcombat", "src", "RealCombat.Host", "bin", "Release", "RimGovernor.RealCombat.Host.dll");
        var game = Path.Combine(repo, ".rimgovernor", "native-rimworld");
        if (!File.Exists(worker))
        {
            throw new AcceptanceException("Build the existing RealCombat.Host Release worker before a hunting sweep.");
        }

        var observed = input.Observed;
        var samples = Enumerable.Range(1, HuntingQualification.PredictionSamples).Select(n => PredictionSeedBase + n).ToArray();
        var request = observed with { Seeds = samples, SamplingReason = "hunt group-versus-single decision" };
        var coldRequest = observed with { Seeds = [samples[0]], SamplingReason = "cold start" };
        var settings = new LocalEncounterOptions("dotnet", worker, game, input.Output,
            request.Identity.GameBuild, request.Identity.Mods, input.PredictorMods, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(2), HuntingQualification.PredictionSamples, 6000, 100, 8, 1048576);
        await using var predictor = new LocalEncounterPredictor(settings);
        var consumer = new EncounterPredictionConsumer(predictor, TimeSpan.FromSeconds(95));
        var cold = await consumer.PredictAsync(coldRequest, () => coldRequest, cancellationToken).ConfigureAwait(false);
        var warm = await consumer.PredictAsync(request, () => request, cancellationToken).ConfigureAwait(false);
        using var cancel = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        var pending = predictor.PredictAsync(request, cancel.Token);
        await cancel.CancelAsync().ConfigureAwait(false);
        var cancelled = await pending.ConfigureAwait(false);
        var cancellationMs = watch.Elapsed.TotalMilliseconds;
        var record = new HuntingCellPrediction(
            input.Cell,
            request,
            warm,
            cold.ColdStartMilliseconds?.Value,
            warm.RequestMilliseconds?.Value,
            cancellationMs
        );
        return new HuntingPredictionRun(record, cold, cancelled);
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Simulation.CombatDispatch;

namespace RimGovernor.Brain.Acceptance;

public static partial class SweepCommand
{
    private static readonly JsonSerializerOptions HuntingJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // Same worker isolation, child case runner, seed dispatcher and retained report directory as ordinary CombatLab.
    private static async Task<int> SweepHuntingAsync(AcceptanceCase c, SweepOptions opts, string load, string[] roots,
        TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (opts.Record || opts.Against.Length != 0 || opts.Variant.Length != 0 || opts.Shots.Length != 0)
        { await stderr.WriteLineAsync("hunting qualification uses retained trial reports, not combat tactic baselines"); return 2; }
        var directory = Path.Combine(opts.Output, c.Name["combatlab/".Length..]);
        try
        {
            await CombatDispatcher.Dispatch<int>(opts.Seeds, opts.Workers, load, async (worker, seed, token) =>
            {
                await stderr.WriteLineAsync($"hunting {c.Name} seed {seed} worker {worker + 1}");
                await RunChildAsync(c, opts, roots[worker], Path.Combine(directory, "seed-" + seed),
                    ["-run-seed", seed.ToString(System.Globalization.CultureInfo.InvariantCulture), "-checkpoint-every", "0"], token);
                return seed;
            }, ct, stopOnFailure: true);
            await stdout.WriteLineAsync("retained hunting trials: " + directory);
            return 0;
        }
        catch (DispatchException<int> e)
        {
            await stderr.WriteLineAsync(e.Message + "; failed and unresolved trial artifacts remain in " + directory);
            return 1;
        }
    }

    private static int HuntingReport(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length != 2)
        { stderr.WriteLine("usage: acceptance sweep -hunting-report <retained-output-directory> <report.json>"); return 2; }
        var trials = new List<HuntingTrial>();
        var predictions = new List<HuntingCellPrediction>();
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.GetFullPath(args[0]), "result.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var data = JsonNode.Parse(File.ReadAllText(file))?.AsObject();
            if (data?["hunting_trial"] is { } trial) trials.Add(trial.Deserialize<HuntingTrial>(HuntingJson) ?? throw new InvalidDataException(file));
            else if (data?["hunting_cell_prediction"] is { } cell) predictions.Add(cell.Deserialize<HuntingCellPrediction>(HuntingJson) ?? throw new InvalidDataException(file));
            else if (file.Contains("hunting-grid-", StringComparison.Ordinal)) missing.Add(file);
        }
        var score = HuntingQualification.Score(predictions, trials);
        if (missing.Count != 0) score = score with { Failures = [.. score.Failures, "missing_trial_artifacts:" + missing.Count] };
        var report = new { score, missingTrials = missing, predictions, trials, qualificationAccepted = false,
            review = "Accuracy alone does not pass runtime, reconstruction or scope review. No provider self-certification." };
        File.WriteAllText(Path.GetFullPath(args[1]), JsonSerializer.Serialize(report, HuntingJson));
        stdout.WriteLine($"hunting trials={score.Trials}, cells={score.Cells.Count}, safe={score.SafeObservations}, false-safe={score.FalseSafe}, inversions={score.RankingInversions}; accuracy={score.AccuracyPassed}");
        return score.AccuracyPassed ? 0 : 1;
    }
}

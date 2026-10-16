using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Acceptance;

/// <summary>Frozen CombatLab hunting experiment (v2): can the predictor tell the planner which hunter set
/// and prey to choose? Research thresholds are not colony risk preferences.</summary>
public static class HuntingQualification
{
    public const string Revision = "hunt-native-v2";
    public const string Domain = "wild-native-hunt/clear/ordinary-rifle/baseliner/drafted-squad";
    public const int NativeTrialsPerCell = 30;
    public const int PredictionSamples = 16;
    public const double CalibrationTolerance = 0.2;
    public const double DurationFactor = 2;

    public static readonly (string Prey, string Animal, int Count)[] PreyKinds =
        [("boar", "WildBoar", 1), ("grizzly", "Bear_Grizzly", 1), ("rhinoceros", "Rhinoceros", 1), ("elephant", "Elephant", 1), ("herd", "WildBoar", 5)];
    public static readonly int[] HunterCounts = [1, 2, 3];
    public static IEnumerable<string> Cells => PreyKinds.SelectMany(p => HunterCounts.Select(h => CellId(p.Prey, h)));
    public static string CellId(string prey, int hunters) => prey + "-h" + hunters;

    // A predeclared, conservative event definition: any new injury needing treatment, missing part,
    // permanent injury, downing or death. Existing injuries only count if worsened.
    public static bool SeriousHarm(EncounterRequest request, EncounterSample outcome) =>
        request.Actors.Where(a => !a.Wild).Any(a =>
        {
            var p = outcome.Actors.Single(x => x.Id == a.Id);
            return p.Dead || p.Downed || p.MissingParts.Except(a.MissingParts, StringComparer.Ordinal).Any() ||
                p.Injuries.GroupBy(i => (i.Definition, i.Part)).Any(group =>
                {
                    var before = a.Health.Where(h => h.Definition == group.Key.Definition && h.Part == group.Key.Part).ToArray();
                    return group.Any(i => i.Tendable || i.Permanent || i.BleedRate > 0) &&
                        (group.Count() > before.Length || group.Sum(i => i.Severity) > before.Sum(h => h.Severity) + 0.0001);
                });
        });

    /// <summary>Every requested prey is dead or downed when the encounter ends.</summary>
    public static bool PreyDowned(EncounterRequest request, EncounterSample outcome) =>
        !outcome.HorizonReached && request.Actors.Where(a => a.Wild).All(a =>
            outcome.Actors.Single(x => x.Id == a.Id) is { } p && (p.Dead || p.Downed));

    /// <summary>The same scene except the observation tick, which differs between independent native runs.</summary>
    public static bool SameScene(EncounterRequest a, EncounterRequest b) =>
        System.Text.Json.JsonSerializer.Serialize(Normalize(a)) == System.Text.Json.JsonSerializer.Serialize(Normalize(b));

    private static EncounterRequest Normalize(EncounterRequest r) =>
        r with { Identity = r.Identity with { ObservationTick = 0 }, Seeds = [], SamplingReason = "" };

    public static HuntingInterval? PredictedDuration(IReadOnlyList<EncounterSample> samples)
    {
        if (samples.Count == 0 || samples.Any(s => s.HorizonReached)) return null;
        var d = samples.Select(s => s.DurationTicks).Order().ToArray();
        return new(d[(int)Math.Floor(.25 * (d.Length - 1))], d[(int)Math.Ceiling(.75 * (d.Length - 1))], d[(d.Length - 1) / 2]);
    }

    private static (double Low, double High) Wilson(int k, int n)
    {
        const double z = 1.96;
        var p = (double)k / n;
        var denominator = 1 + z * z / n;
        var centre = (p + z * z / (2 * n)) / denominator;
        var half = z * Math.Sqrt(p * (1 - p) / n + z * z / (4.0 * n * n)) / denominator;
        return (centre - half, centre + half);
    }

    private static bool ProvenanceValid(HuntingTrial t) => t.Protocol == Revision && Cells.Contains(t.Cell, StringComparer.Ordinal) &&
        t.TrialId.Length != 0 && t.NativeRunId.Length != 0 && t.NativeBuild.Length != 0 && t.NativeMods.Length != 0 &&
        t.NativeBuild == t.Request.Identity.GameBuild && t.NativeMods == t.Request.Identity.Mods &&
        t.InitialStateMatched && t.RevengeEnabled && t.Request.Environment is { } environment &&
        double.IsFinite(environment.OutdoorTemperature) && double.IsFinite(environment.SkyGlow) && environment.SkyGlow is >= 0 and <= 1 &&
        (t.Observed is null || t.Observed.Seed == t.NativeSeed);

    private static bool PredictionValid(HuntingCellPrediction c) => Cells.Contains(c.Cell, StringComparer.Ordinal) &&
        c.Request.Seeds.Count == PredictionSamples && c.Prediction.Status == EncounterStatus.Evidence &&
        c.Prediction.EngineFingerprint.Length != 0 && c.Prediction.Samples.Select(s => s.Seed).SequenceEqual(c.Request.Seeds) &&
        c.Request.Environment is not null;

    public static HuntingQualificationReport Score(IReadOnlyList<HuntingCellPrediction> predictions, IReadOnlyList<HuntingTrial> trials)
    {
        var failures = new List<string>();
        var cells = new List<HuntingCellScore>();
        var safeObservations = 0;
        var falseSafe = 0;
        if (trials.Count == 0) failures.Add("no_trials");
        if (trials.Select(t => t.NativeBuild + ":" + t.NativeMods).Concat(predictions.Select(p => p.Request.Identity.GameBuild + ":" + p.Request.Identity.Mods))
                .Distinct(StringComparer.Ordinal).Count() > 1 ||
            predictions.Select(p => p.Prediction.EngineFingerprint).Distinct(StringComparer.Ordinal).Count() > 1) failures.Add("mixed_provenance");
        if (trials.Select(t => t.TrialId).Distinct(StringComparer.Ordinal).Count() != trials.Count ||
            trials.Select(t => t.NativeRunId).Distinct(StringComparer.Ordinal).Count() != trials.Count) failures.Add("duplicate_native_trial");
        if (!trials.Any(t => t.RetaliationObserved)) failures.Add("revenge_not_observed");
        var nativeSeeds = trials.Select(t => t.NativeSeed).ToHashSet();
        foreach (var cell in Cells)
        {
            var matches = predictions.Where(p => p.Cell == cell).ToArray();
            if (matches.Length != 1) { failures.Add((matches.Length == 0 ? "missing_prediction:" : "duplicate_prediction:") + cell); continue; }
            var prediction = matches[0];
            if (!PredictionValid(prediction) || prediction.Request.Seeds.Any(nativeSeeds.Contains)) { failures.Add(cell + ":invalid_prediction"); continue; }
            if (prediction.ColdStartMilliseconds is null or > 15000 || prediction.CancellationMilliseconds is null or > 2000 ||
                prediction.WarmMilliseconds is null or > 20000 || prediction.WarmMilliseconds / PredictionSamples > 1000) failures.Add(cell + ":runtime");
            var valid = new List<HuntingTrial>();
            foreach (var t in trials.Where(t => t.Cell == cell))
            {
                if (!ProvenanceValid(t) || !SameScene(t.Request, prediction.Request)) { failures.Add(t.TrialId + ":invalid_provenance"); continue; }
                if (t.Observed is null) { failures.Add(t.TrialId + ":unresolved"); continue; }
                valid.Add(t);
            }
            if (valid.Count < NativeTrialsPerCell) { failures.Add("insufficient_native_trials:" + cell); continue; }
            var samples = prediction.Prediction.Samples;
            var harmP = samples.Count(s => SeriousHarm(prediction.Request, s)) / (double)samples.Count;
            var downP = samples.Count(s => PreyDowned(prediction.Request, s)) / (double)samples.Count;
            var harmObserved = valid.Count(t => SeriousHarm(t.Request, t.Observed!));
            var downObserved = valid.Count(t => PreyDowned(t.Request, t.Observed!));
            if (harmP == 0) { safeObservations += valid.Count; falseSafe += harmObserved; }
            foreach (var t in valid.Where(t => t.Request.Actors.Where(a => !a.Wild).Any(a => t.Observed!.Actors.Single(p => p.Id == a.Id).Dead &&
                !samples.Any(s => s.Actors.Single(p => p.Id == a.Id).Dead))))
                failures.Add(cell + ":missed_death");
            var native = valid.Select(t => t.Observed!.DurationTicks).Order().ToArray();
            var nativeMedian = native[(native.Length - 1) / 2];
            var interval = PredictedDuration(samples);
            var durationOk = interval is not null && nativeMedian >= interval.Lower / DurationFactor && nativeMedian <= interval.Upper * DurationFactor;
            var harmRate = harmObserved / (double)valid.Count;
            var downRate = downObserved / (double)valid.Count;
            var score = new HuntingCellScore(cell, valid.Count, harmP, harmRate, downP, downRate, interval, nativeMedian,
                Math.Abs(harmP - harmRate) <= CalibrationTolerance, Math.Abs(downP - downRate) <= CalibrationTolerance, durationOk);
            if (!score.HarmCalibrated) failures.Add("harm_calibration:" + cell);
            if (!score.DownedCalibrated) failures.Add("downed_calibration:" + cell);
            if (!score.DurationWithinTolerance) failures.Add("duration:" + cell);
            cells.Add(score);
        }
        // Rank only cells whose observed severe-harm rates separate (non-overlapping 95% Wilson intervals);
        // the predicted order must agree strictly, so a tie does not discriminate.
        var ranked = 0;
        var inversions = new List<string>();
        for (var i = 0; i < cells.Count; i++) for (var j = i + 1; j < cells.Count; j++)
        {
            var a = cells[i]; var b = cells[j];
            var wa = Wilson((int)Math.Round(a.ObservedHarm * a.Trials), a.Trials); var wb = Wilson((int)Math.Round(b.ObservedHarm * b.Trials), b.Trials);
            var direction = wa.High < wb.Low ? -1 : wb.High < wa.Low ? 1 : 0;
            if (direction == 0) continue;
            ranked++;
            if (Math.Sign(a.PredictedHarm - b.PredictedHarm) != direction) inversions.Add(a.Cell + "|" + b.Cell);
        }
        if (ranked == 0) failures.Add("no_separated_ranking_pair");
        failures.AddRange(inversions.Select(p => "ranking:" + p));
        double? upper = safeObservations == 0 || falseSafe != 0 ? null : 1 - Math.Pow(0.05, 1.0 / safeObservations);
        if (falseSafe != 0) failures.Add("false_safe_observed");
        if (upper is null || upper > .01) failures.Add("false_safe_bound");
        return new(Revision, trials.Count, cells, safeObservations, falseSafe, upper, ranked, inversions.Count, failures.Distinct().ToArray());
    }
}

public sealed record HuntingInterval(int Lower, int Upper, int Median);
public sealed record HuntingCellScore(string Cell, int Trials, double PredictedHarm, double ObservedHarm, double PredictedDowned,
    double ObservedDowned, HuntingInterval? PredictedDuration, int NativeMedianDuration, bool HarmCalibrated, bool DownedCalibrated,
    bool DurationWithinTolerance);

/// <summary>One predictor request per cell: 16 independent seeds, with cold/warm/cancel measurements.</summary>
public sealed record HuntingCellPrediction(string Cell, EncounterRequest Request, EncounterPrediction Prediction,
    double? ColdStartMilliseconds, double? WarmMilliseconds, double? CancellationMilliseconds);

/// <summary>One independent native encounter of a cell.</summary>
public sealed record HuntingTrial(string TrialId, string Protocol, string Cell, string NativeRunId, int NativeSeed,
    string NativeBuild, string NativeMods, bool InitialStateMatched, bool RevengeEnabled, EncounterRequest Request, EncounterSample? Observed)
{
    public bool RetaliationObserved { get; init; }
}

public sealed record HuntingQualificationReport(string Protocol, int Trials, IReadOnlyList<HuntingCellScore> Cells,
    int SafeObservations, int FalseSafe, double? FalseSafeUpper95, int SeparatedPairs, int RankingInversions, IReadOnlyList<string> Failures)
{
    public bool AccuracyPassed => Failures.Count == 0;
}

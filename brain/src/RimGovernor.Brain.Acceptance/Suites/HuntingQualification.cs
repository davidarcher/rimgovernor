using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Acceptance;

/// <summary>Frozen CombatLab hunting experiment. Research thresholds are not colony risk preferences.</summary>
public static class HuntingQualification
{
    public const string Revision = "hunt-native-v1";
    public const string Domain = "wild-native-hunt/clear/ordinary-rifle/baseliner";
    public static readonly string[] RequiredStrata = ["baseline", "health", "distance", "terrain", "herd", "departure"];

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

    /// <summary>Split-conformal absolute relative residual: training outcomes alone set the radius.
    /// The ceil(.9*(n+1)) rank gives marginal 90% coverage under exchangeability; held-out data tests transfer.</summary>
    public static double? CalibrationRadius(IReadOnlyList<HuntingTrial> calibration)
    {
        if (calibration.Count < 19 || calibration.Any(t => t.HeldOut || t.Observed is null || t.Observed.HorizonReached ||
            t.Prediction.Status != EncounterStatus.Evidence || t.Prediction.Samples.Count == 0 || t.Prediction.Samples.Any(s => s.HorizonReached))) return null;
        var residuals = calibration.Select(t => Math.Abs(t.Observed!.DurationTicks - Median(t.Prediction.Samples)) /
            (double)Math.Max(1, Median(t.Prediction.Samples))).Order().ToArray();
        return residuals[(int)Math.Ceiling(.9 * (residuals.Length + 1)) - 1];
    }

    public static HuntingInterval? Interval(IReadOnlyList<EncounterSample> samples, double? radius)
    {
        if (radius is null || !double.IsFinite(radius.Value) || radius < 0 || samples.Count == 0 ||
            samples.Select(s => s.Seed).Distinct().Count() != samples.Count || samples.Any(s => s.HorizonReached)) return null;
        var median = Median(samples);
        return new((int)Math.Floor(Math.Max(0, median - Math.Max(1, median) * radius.Value)),
            (int)Math.Ceiling(median + Math.Max(1, median) * radius.Value), median);
    }

    private static int Median(IReadOnlyList<EncounterSample> samples) => samples.Select(s => s.DurationTicks).Order().ElementAt((samples.Count - 1) / 2);
    private static bool ProvenanceValid(HuntingTrial t) => t.Protocol == Revision && RequiredStrata.Contains(t.Stratum, StringComparer.Ordinal) &&
        t.TrialId.Length != 0 && t.NativeRunId.Length != 0 && t.NativeBuild.Length != 0 && t.NativeMods.Length != 0 && t.NativeBuild == t.Request.Identity.GameBuild && t.NativeMods == t.Request.Identity.Mods &&
        t.InitialStateMatched && t.RevengeEnabled && t.Request.Environment is { } environment &&
        double.IsFinite(environment.OutdoorTemperature) && double.IsFinite(environment.SkyGlow) && environment.SkyGlow is >= 0 and <= 1 &&
        !t.Request.Seeds.Contains(t.NativeSeed) &&
        (t.Prediction.Status != EncounterStatus.Evidence || t.Prediction.EngineFingerprint.Length != 0 &&
            t.Prediction.Samples.Select(s => s.Seed).SequenceEqual(t.Request.Seeds)) &&
        (t.Observed is null || t.Observed.Seed == t.NativeSeed);
    public static HuntingQualificationReport Score(IReadOnlyList<HuntingTrial> trials)
    {
        var failures = new List<string>();
        var eligible = new List<(HuntingTrial Trial, HuntingInterval Interval)>();
        var safe = 0;
        var falseSafe = 0;
        var rejected = 0;
        var unresolved = 0;
        if (trials.Count == 0) failures.Add("no_trials");
        if (trials.Select(t => t.NativeBuild + ":" + t.NativeMods).Distinct(StringComparer.Ordinal).Count() > 1 ||
            trials.Where(t => t.Prediction.Status == EncounterStatus.Evidence).Select(t => t.Prediction.EngineFingerprint).Distinct(StringComparer.Ordinal).Count() > 1) failures.Add("mixed_provenance");
        if (!trials.Any(t => t.HeldOut && t.RetaliationObserved)) failures.Add("revenge_not_observed");
        if (trials.Select(t => t.TrialId).Distinct(StringComparer.Ordinal).Count() != trials.Count ||
            trials.Select(t => t.NativeRunId).Distinct(StringComparer.Ordinal).Count() != trials.Count ||
            trials.Select(t => t.NativeSeed).Distinct().Count() != trials.Count) failures.Add("duplicate_native_trial");
        var calibration = trials.Where(t => !t.HeldOut && ProvenanceValid(t)).GroupBy(t => t.Stratum)
            .ToDictionary(g => g.Key, g => CalibrationRadius(g.ToArray()), StringComparer.Ordinal);
        foreach (var t in trials)
        {
            if (!ProvenanceValid(t))
            { failures.Add(t.TrialId + ":invalid_provenance"); continue; }
            if (!t.HeldOut) continue; // Calibration cannot enter held-out denominators.
            if (t.Prediction.Status != EncounterStatus.Evidence) { rejected++; continue; }
            if (t.Observed is null || t.Observed.HorizonReached ||
                Interval(t.Prediction.Samples, calibration.GetValueOrDefault(t.Stratum)) is not { } interval)
            { unresolved++; continue; }
            if (!t.Request.Actors.Select(a => a.Id).Order().SequenceEqual(t.Observed.Actors.Select(a => a.Id).Order()) ||
                t.Prediction.Samples.Any(s => !t.Request.Actors.Select(a => a.Id).Order().SequenceEqual(s.Actors.Select(a => a.Id).Order())))
            { failures.Add(t.TrialId + ":actors_mismatch"); continue; }
            if (t.Request.Actors.Any(a => a.Departure is { } d &&
                (t.Observed.Actors.Single(p => p.Id == a.Id).LastShotTick >= d.Tick ||
                 t.Prediction.Samples.Any(s => s.Actors.Single(p => p.Id == a.Id).LastShotTick >= d.Tick))))
                failures.Add(t.TrialId + ":contribution_after_departure");
            var predictedSafe = !t.Prediction.Samples.Any(s => SeriousHarm(t.Request, s));
            if (predictedSafe)
            {
                safe++;
                if (SeriousHarm(t.Request, t.Observed)) falseSafe++;
            }
            if (t.Request.Actors.Where(a => !a.Wild).Any(a => t.Observed.Actors.Single(p => p.Id == a.Id).Dead &&
                !t.Prediction.Samples.Any(s => s.Actors.Single(p => p.Id == a.Id).Dead))) failures.Add(t.TrialId + ":missed_death");
            eligible.Add((t, interval));
        }
        var errors = eligible.Select(e => Math.Abs(e.Interval.Median - e.Trial.Observed!.DurationTicks) / (double)Math.Max(1, e.Trial.Observed!.DurationTicks)).Order().ToArray();
        double? median = errors.Length == 0 ? null : (errors[(errors.Length - 1) / 2] + errors[errors.Length / 2]) / 2;
        double? coverage = eligible.Count == 0 ? null : eligible.Count(e => e.Trial.Observed!.DurationTicks >= e.Interval.Lower && e.Trial.Observed.DurationTicks <= e.Interval.Upper) / (double)eligible.Count;
        double? upper = safe == 0 || falseSafe != 0 ? null : 1 - Math.Pow(0.05, 1.0 / safe);
        if (median is null || median > .2) failures.Add("duration_error");
        if (coverage is null || coverage < .9) failures.Add("interval_coverage");
        if (falseSafe != 0) failures.Add("false_safe_observed");
        if (upper is null || upper > .01) failures.Add("false_safe_bound");
        foreach (var stratum in RequiredStrata)
        {
            var count = eligible.Count(e => e.Trial.Stratum == stratum);
            if (count == 0) failures.Add("missing_stratum:" + stratum);
            else if (count < 60) failures.Add("insufficient_held_out:" + stratum);
        }
        // Rank only groups with separated observed min/max envelopes; overlap is explicitly unresolved.
        var groups = eligible.GroupBy(e => e.Trial.ComparisonGroup).Where(g => g.Key.Length > 0).ToArray();
        var ranked = 0;
        foreach (var group in groups)
        {
            var variants = group.GroupBy(e => e.Trial.Variant).ToArray();
            for (var i = 0; i < variants.Length; i++) for (var j = i + 1; j < variants.Length; j++)
            {
                var a = variants[i].ToArray(); var b = variants[j].ToArray();
                var direction = a.Max(e => e.Trial.Observed!.DurationTicks) < b.Min(e => e.Trial.Observed!.DurationTicks) ? -1 :
                    b.Max(e => e.Trial.Observed!.DurationTicks) < a.Min(e => e.Trial.Observed!.DurationTicks) ? 1 : 0;
                if (direction == 0) continue;
                ranked++;
                if (Math.Sign(a.Average(e => e.Interval.Median) - b.Average(e => e.Interval.Median)) != direction) failures.Add("ranking:" + group.Key);
            }
        }
        if (ranked == 0) failures.Add("no_separated_ranking_pair");
        return new(Revision, trials.Count, eligible.Count, rejected, unresolved, safe, falseSafe, upper, median, coverage, ranked, failures.Distinct().ToArray());
    }
}

public sealed record HuntingInterval(int Lower, int Upper, int Median);
public sealed record HuntingTrial(string TrialId, string Protocol, string Stratum, bool HeldOut, string ComparisonGroup,
    string Variant, string NativeRunId, int NativeSeed, string NativeBuild, string NativeMods,
    bool InitialStateMatched, bool RevengeEnabled, EncounterRequest Request, EncounterPrediction Prediction, EncounterSample? Observed)
{
    public bool RetaliationObserved { get; init; }
}
public sealed record HuntingQualificationReport(string Protocol, int Trials, int Applicable, int Rejected, int Unresolved,
    int SafePredictions, int FalseSafe, double? FalseSafeUpper95, double? MedianDurationError, double? IntervalCoverage,
    int SeparatedPairs, IReadOnlyList<string> Failures)
{
    public bool AccuracyPassed => Failures.Count == 0;
}

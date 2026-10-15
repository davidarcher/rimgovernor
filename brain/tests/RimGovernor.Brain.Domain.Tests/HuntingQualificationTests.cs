using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.Domain;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class HuntingQualificationTests
{
    [Fact]
    public void ReplayAndTinySamplesCannotQualify()
    {
        Assert.Null(HuntingQualification.Interval([Sample(1)], null));
        var report = HuntingQualification.Score([Trial(1)]);
        Assert.Contains("false_safe_bound", report.Failures);
        Assert.False(report.AccuracyPassed);
    }

    [Fact]
    public void RepeatedNativeSeedsAndPredictionSeedLeaksInvalidateEvidence()
    {
        var t = Trial(1);
        Assert.Contains("duplicate_native_trial", HuntingQualification.Score([t, t with { TrialId = "different" }]).Failures);
        Assert.Contains("t1:invalid_provenance", HuntingQualification.Score([t with { NativeSeed = 1001 }]).Failures);
    }

    [Fact]
    public void AbstentionsAndCalibrationNeverBecomeSafeEvidence()
    {
        var t = Trial(1);
        var rejected = t with { Prediction = t.Prediction with { Status = EncounterStatus.Unsupported, Samples = [] } };
        var report = HuntingQualification.Score([rejected, Trial(2) with { HeldOut = false }, Trial(3) with { Observed = null }]);
        Assert.Equal(0, report.SafePredictions);
        Assert.Equal(1, report.Rejected);
        Assert.Equal(1, report.Unresolved);
    }

    [Fact]
    public void ZeroFalseSafeNeeds299IndependentPredictionsAndEveryOtherGate()
    {
        var report298 = HuntingQualification.Score(Calibration().Concat(Enumerable.Range(1, 298).Select(Trial)).ToArray());
        var report299 = HuntingQualification.Score(Calibration().Concat(Enumerable.Range(1, 299).Select(Trial)).ToArray());
        Assert.Contains("false_safe_bound", report298.Failures);
        Assert.DoesNotContain("false_safe_bound", report299.Failures);
        Assert.Contains("missing_stratum:health", report299.Failures);
        Assert.Contains("no_separated_ranking_pair", report299.Failures);
        Assert.False(report299.AccuracyPassed);
    }

    [Fact]
    public void ActualNewTendableInjuryFailsEvenWith299OtherwiseSafeTrials()
    {
        var trials = Enumerable.Range(1, 299).Select(Trial).ToArray();
        var first = trials[0];
        trials[0] = first with { Observed = first.Observed! with { Actors = [Actor() with {
            Injuries = [new("Gunshot", "left arm", 1, 0, true, false, false)] }] } };
        var report = HuntingQualification.Score(Calibration().Concat(trials).ToArray());
        Assert.Equal(1, report.FalseSafe);
        Assert.Contains("false_safe_observed", report.Failures);
    }

    [Fact]
    public void TrainingAloneSetsIntervalAndHeldOutLeakageIsRejected()
    {
        var calibration = Calibration().ToArray();
        Assert.Equal(0, HuntingQualification.CalibrationRadius(calibration));
        Assert.Null(HuntingQualification.CalibrationRadius(calibration.Take(18).ToArray()));
        Assert.Null(HuntingQualification.CalibrationRadius(calibration.Select(t => t with { HeldOut = true }).ToArray()));
        Assert.Equal(new HuntingInterval(100, 100, 100), HuntingQualification.Interval([Sample(1)], 0));
        Assert.Null(HuntingQualification.Interval([Sample(1) with { HorizonReached = true }], 0));
    }

    [Fact]
    public void MissingEnvironmentOrEngineIdentityCannotEnterCalibrationOrHoldout()
    {
        var trial = Trial(1);
        Assert.Contains("t1:invalid_provenance", HuntingQualification.Score([trial with { Request = trial.Request with { Environment = null } }]).Failures);
        Assert.Contains("t1:invalid_provenance", HuntingQualification.Score([trial with { Prediction = trial.Prediction with { EngineFingerprint = "" } }]).Failures);
        var malformed = trial with { Prediction = trial.Prediction with { Samples = [Sample(12)] } };
        Assert.Contains("t1:invalid_provenance", HuntingQualification.Score([malformed]).Failures);
    }

    [Fact]
    public void CompleteHeldOutEvidenceCanPassButWrongSeparatedRankingCannot()
    {
        var trials = HuntingQualification.RequiredStrata.SelectMany((stratum, index) =>
            Enumerable.Range(1, 90).Select(n =>
            {
                var trial = Trial(30000 + index * 100 + n);
                var duration = 100 + index * 100;
                return trial with { Stratum = stratum, HeldOut = n > 30, RetaliationObserved = true,
                    ComparisonGroup = "matched", Variant = stratum,
                    Observed = trial.Observed! with { DurationTicks = duration },
                    Prediction = trial.Prediction with { Samples = trial.Prediction.Samples.Select(p => p with { DurationTicks = duration }).ToArray() } };
            })).ToArray();
        var passed = HuntingQualification.Score(trials);
        Assert.True(passed.AccuracyPassed, string.Join(", ", passed.Failures));
        Assert.Equal(360, passed.Applicable);
        Assert.Equal(360, passed.SafePredictions);
        var unbalanced = trials.Where(t => !t.HeldOut || t.Stratum != "health" || t.NativeSeed == 30131).ToArray();
        var unbalancedReport = HuntingQualification.Score(unbalanced);
        Assert.Equal(301, unbalancedReport.SafePredictions);
        Assert.Contains("insufficient_held_out:health", unbalancedReport.Failures);
        var reversed = trials.Select(t => t.Stratum == "baseline" ? t with {
            Prediction = t.Prediction with { Samples = t.Prediction.Samples.Select(p => p with { DurationTicks = 700 }).ToArray() } } : t).ToArray();
        Assert.Contains("ranking:matched", HuntingQualification.Score(reversed).Failures);
    }

    [Fact]
    public void ExistingWoundCannotHideAnotherWoundOnTheSameBodyPart()
    {
        var trial = Trial(1);
        var request = trial.Request with { Actors = [trial.Request.Actors[0] with { Health = [new("Cut", "arm", 3)] }] };
        var unchanged = Sample(1) with { Actors = [Actor() with { Injuries = [new("Cut", "arm", 3, 0, true, false, false)] }] };
        var another = unchanged with { Actors = [Actor() with { Injuries = [
            new("Cut", "arm", 2.8, 0, true, false, false), new("Cut", "arm", .2, 0, true, false, false)] }] };
        Assert.False(HuntingQualification.SeriousHarm(request, unchanged));
        Assert.True(HuntingQualification.SeriousHarm(request, another));
    }

    private static IEnumerable<HuntingTrial> Calibration() => Enumerable.Range(20001, 19).Select(i => Trial(i) with { HeldOut = false });
    private static EncounterActorOutcome Actor() => new("h", false, false, 1, 0, false, [], [], [], null, null, null, null, null);
    private static EncounterSample Sample(int seed) => new(seed, 100, false, [Actor()]);
    private static HuntingTrial Trial(int id)
    {
        var seeds = Enumerable.Range(1001, 19).ToArray();
        var request = new EncounterRequest { Identity = new("world", 1, "build", "mods", "native-hunt/timed-departure"),
            Width = 100, Height = 100, Terrain = "Soil", Weather = "Clear", Environment = new(21, 1), HorizonTicks = 6000,
            Actors = [new EncounterActor { Id = "h", Kind = "Colonist", Wild = false, X = 50, Z = 50 }],
            Seeds = seeds, SamplingReason = "qualification" };
        return new("t" + id, HuntingQualification.Revision, "baseline", true, "", "default", "native-" + id, id,
            "build", "mods", true, true, request, new EncounterPrediction { Status = EncounterStatus.Evidence,
                RequestFingerprint = "fixture", EngineFingerprint = "engine", Reason = "", Samples = seeds.Select(Sample).ToArray() }, Sample(id));
    }
}

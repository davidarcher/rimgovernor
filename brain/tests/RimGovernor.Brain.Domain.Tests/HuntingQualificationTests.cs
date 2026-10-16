using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.Domain;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class HuntingQualificationTests
{
    [Fact]
    public void MissingEvidenceCannotQualify()
    {
        var report = HuntingQualification.Score([], []);
        Assert.Contains("no_trials", report.Failures);
        Assert.Contains("missing_prediction:boar-h1", report.Failures);
        Assert.False(report.AccuracyPassed);
    }

    [Fact]
    public void ZeroFalseSafeNeeds299SafeObservations()
    {
        // Only the three-hunter cells are safe: 5 cells x 30 trials = 150 safe observations.
        var (predictions, trials) = Grid(harmed: cell => !cell.EndsWith("h3", StringComparison.Ordinal));
        var report = HuntingQualification.Score(predictions, trials);
        Assert.Equal(150, report.SafeObservations);
        Assert.Contains("false_safe_bound", report.Failures);
        var (allSafe, safeTrials) = Grid(harmed: _ => false);
        var full = HuntingQualification.Score(allSafe, safeTrials);
        Assert.Equal(450, full.SafeObservations);
        Assert.DoesNotContain("false_safe_bound", full.Failures);
    }

    [Fact]
    public void ObservedHarmWhereEverySampleWasSafeIsFalseSafe()
    {
        var (predictions, trials) = Grid(harmed: _ => false);
        trials[0] = trials[0] with { Observed = Outcome(trials[0].NativeSeed, harm: true) };
        var report = HuntingQualification.Score(predictions, trials);
        Assert.Equal(1, report.FalseSafe);
        Assert.Contains("false_safe_observed", report.Failures);
    }

    [Fact]
    public void SeparatedCellsMustBeRankedByPredictedHarm()
    {
        // Three-hunter cells are safe natively; one-hunter cells are always harmed.
        var (predictions, trials) = Grid(harmed: cell => cell.EndsWith("h1", StringComparison.Ordinal));
        var ranked = HuntingQualification.Score(predictions, trials);
        Assert.True(ranked.SeparatedPairs > 0);
        Assert.Equal(0, ranked.RankingInversions);
        var swapped = predictions.Select(p => p.Cell.EndsWith("h1", StringComparison.Ordinal) ? p with { Prediction = Predict(p.Request, harm: false) }
            : p.Cell.EndsWith("h3", StringComparison.Ordinal) ? p with { Prediction = Predict(p.Request, harm: true) } : p).ToArray();
        var wrong = HuntingQualification.Score(swapped, trials);
        Assert.True(wrong.RankingInversions > 0);
        Assert.Contains(wrong.Failures, f => f.StartsWith("ranking:", StringComparison.Ordinal));
        Assert.Contains(wrong.Failures, f => f.StartsWith("harm_calibration:", StringComparison.Ordinal));
    }

    [Fact]
    public void DurationOutsideTheWidenedInterquartileRangeFails()
    {
        var (predictions, trials) = Grid(harmed: _ => false);
        var slow = trials.Select(t => t.Cell == "boar-h1" ? t with { Observed = t.Observed! with { DurationTicks = 5000 } } : t).ToArray();
        Assert.Contains("duration:boar-h1", HuntingQualification.Score(predictions, slow).Failures);
    }

    [Fact]
    public void RuntimeProvenanceAndSceneMismatchesAreRetainedFailures()
    {
        var (predictions, trials) = Grid(harmed: _ => false);
        var slow = predictions.Select(p => p.Cell == "boar-h1" ? p with { WarmMilliseconds = 20001 } : p).ToArray();
        Assert.Contains("boar-h1:runtime", HuntingQualification.Score(slow, trials).Failures);
        var changed = trials.Select(t => t.Cell == "boar-h1" ? t with { Request = t.Request with { Terrain = "Sand" } } : t).ToArray();
        Assert.Contains(HuntingQualification.Score(predictions, changed).Failures, f => f.EndsWith(":invalid_provenance", StringComparison.Ordinal));
        var leaked = predictions.Select(p => p.Cell == "boar-h1" ? p with { Request = p.Request with { Seeds = [.. p.Request.Seeds.Take(15), trials[0].NativeSeed] } } : p).ToArray();
        Assert.Contains("boar-h1:invalid_prediction", HuntingQualification.Score(leaked, trials).Failures);
        var revengeless = trials.Select(t => t with { RetaliationObserved = false }).ToArray();
        Assert.Contains("revenge_not_observed", HuntingQualification.Score(predictions, revengeless).Failures);
    }

    [Fact]
    public void ObservationTickDoesNotSeparateScenesButHealthDoes()
    {
        var request = Request([1]);
        Assert.True(HuntingQualification.SameScene(request, request with { Identity = request.Identity with { ObservationTick = 99 } }));
        Assert.False(HuntingQualification.SameScene(request, request with { Actors = [request.Actors[0] with { X = 1 }, request.Actors[1]] }));
    }

    [Fact]
    public void ExistingWoundCannotHideAnotherWoundOnTheSameBodyPart()
    {
        var request = Request([1]) with { Actors = [Request([1]).Actors[0] with { Health = [new("Cut", "arm", 3)] }] };
        var unchanged = Sample(1) with { Actors = [Actor("h0") with { Injuries = [new("Cut", "arm", 3, 0, true, false, false)] }] };
        var another = unchanged with { Actors = [Actor("h0") with { Injuries = [
            new("Cut", "arm", 2.8, 0, true, false, false), new("Cut", "arm", .2, 0, true, false, false)] }] };
        Assert.False(HuntingQualification.SeriousHarm(request, unchanged));
        Assert.True(HuntingQualification.SeriousHarm(request, another));
    }

    [Fact]
    public void CensoredSamplesStillYieldADurationInterval()
    {
        var samples = Enumerable.Range(0, 16).Select(i => Sample(i) with { DurationTicks = i < 14 ? 400 : 6000, HorizonReached = i >= 14 }).ToArray();
        Assert.Equal(new HuntingInterval(400, 400, 400), HuntingQualification.PredictedDuration(samples));
        Assert.Null(HuntingQualification.PredictedDuration([]));
    }

    [Fact]
    public void PreyDownedRequiresEveryPreyDownedBeforeTheHorizon()
    {
        var request = Request([1]);
        Assert.True(HuntingQualification.PreyDowned(request, Sample(1)));
        Assert.False(HuntingQualification.PreyDowned(request, Sample(1) with { HorizonReached = true }));
        Assert.False(HuntingQualification.PreyDowned(request, Sample(1) with { Actors = [Actor("h0"), Actor("p0") with { Downed = false }] }));
    }

    private static EncounterActorOutcome Actor(string id) => new(id, id == "p0", false, 1, 0, false, [], [], [], null, null, null, null, null);
    private static EncounterSample Sample(int seed, bool harm = false) => new(seed, 400, false,
        [Actor("h0") with { Downed = harm }, Actor("p0")]);
    private static EncounterSample Outcome(int seed, bool harm) => Sample(seed, harm);
    private static EncounterRequest Request(IReadOnlyList<int> seeds) => new()
    {
        Identity = new("world", 1, "build", "mods", "native-hunt/timed-departure"),
        Width = 100, Height = 100, Terrain = "Soil", Weather = "Clear", Environment = new(21, 1), HorizonTicks = 6000,
        Actors = [new EncounterActor { Id = "h0", Kind = "Colonist", Wild = false, X = 45, Z = 40 },
            new EncounterActor { Id = "p0", Kind = "WildBoar", Wild = true, X = 45, Z = 52 }],
        Seeds = seeds, SamplingReason = "test",
    };
    private static EncounterPrediction Predict(EncounterRequest request, bool harm) => new()
    {
        Status = EncounterStatus.Evidence, RequestFingerprint = "fp", EngineFingerprint = "engine", Reason = "",
        Samples = request.Seeds.Select(s => Sample(s, harm)).ToArray(),
    };

    private static (HuntingCellPrediction[] Predictions, HuntingTrial[] Trials) Grid(Func<string, bool> harmed, int trialsPerCell = 30)
    {
        var seed = 0;
        var predictions = new List<HuntingCellPrediction>();
        var trials = new List<HuntingTrial>();
        foreach (var cell in HuntingQualification.Cells)
        {
            var request = Request(Enumerable.Range(1000001, HuntingQualification.PredictionSamples).ToArray());
            predictions.Add(new(cell, request, Predict(request, harmed(cell)), 5000, 12000, 500));
            for (var i = 0; i < trialsPerCell; i++)
            {
                seed++;
                trials.Add(new HuntingTrial(cell + ":" + seed, HuntingQualification.Revision, cell, "run" + seed, seed, "build", "mods", true, true,
                    Request([1]) with { Identity = Request([1]).Identity with { ObservationTick = seed } }, Outcome(seed, harmed(cell)))
                { RetaliationObserved = true });
            }
        }
        return (predictions.ToArray(), trials.ToArray());
    }
}

using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Telemetry.Otlp;
using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastLiveEvidenceTests
{
    private static readonly GenerationSnapshot Scope = new(new("colony"), new(1), new("load"), new("root"), new(1), new(1));
    private static NeedsForecastRead Needs(long tick, double food = .6) => new() { Tick = tick, Pawns = [new() {
        Pawn = "pawn", Mood = Known(.7), MoodTarget = Known(.99), Needs = [new("food", Known(food), default, default), new("rest", Known(.8), default, default)] }] };
    private static ForecastCoupledContext Context(long tick) => new() { Activities = new(), Needs = Needs(tick), Resources = new(new(), new([], [], [], []), default) };
    private static ForecastCoupledSelection Selection()
    {
        var plan = new ForecastActivityPlan([new("task", "pawn", 0, 0, 0, 50, 0, 0, 50, 50, 0)], 50, 50);
        var needs = new ForecastNeedsResult([new("food", Known(.4), default, default, default), new("rest", Known(.5), default, default, default)],
            default, default, new(new("pawn", "place", default, default), []), []) { Mood = new([], default, default, default, default, Known(.6), []) };
        var outcome = new ForecastCoupledOutcome(plan, Known(true), new Dictionary<string, ForecastNeedsResult> { ["pawn"] = needs }, []);
        var vector = ForecastCoupledSelection.CoupledConsequences(outcome, []);
        return new(new(["task"], plan, default, [], 0, false), outcome, outcome, vector, vector, [], 100, false);
    }
    private static PawnActivities Batch => new(1, [new("pawn", "assignment", "method", 0, 1, false,
        [new(PawnActivityKind.Eat, "food", "", "", 1, 1000)]) { Task = "task" }]) {
        Tasks = [new("task", "pawn", [], new(PawnActivityKind.Eat, "food", "", "", 1, 1000), new(1, 1), 100)] };
    private static ActivityTaskRead Read(long tick, string state, string method = "method", long revision = 1) => new() {
        Known = true, Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 1 }, Tick = tick },
        Pawns = [new("pawn", new("assignment", method, revision, 0, state, "", null, tick))] };

    [Fact]
    public void ProductionOwnerUsesSelectedValuesAndFreshActualMoodOnceAcrossOverlappingReviews()
    {
        var predictions = new List<ForecastPrediction>(); var observations = new List<ForecastObservation>();
        var owner = new ForecastEvidencePending(predictions.Add, observations.Add);
        var selected = Selection(); var comparison = selected.Selection;
        owner.Review(Scope, 100, Needs(100), Context(100));
        owner.Capture(Scope, 100, 1, selected, Context(100), ForecastAdmission.Admitted, [Batch], "method");
        owner.Capture(Scope, 100, 1, selected, Context(100), ForecastAdmission.Admitted, [Batch], "method");
        owner.Capture(Scope, 100, 2, selected, Context(100), ForecastAdmission.Continued, [Batch], "method");
        owner.Review(Scope, 200, Needs(200), Context(200));
        owner.Review(Scope, 200, Needs(200), Context(200));
        var rows = ForecastResiduals.Compare(predictions, observations);
        Assert.Equal(2, rows.Count(r => r.Prediction.Key.Metric == ForecastMetric.FoodLevel));
        Assert.All(rows.Where(r => r.Prediction.Key.Metric == ForecastMetric.FoodLevel), r => Assert.Equal(.2, r.Scalar!.Value, 10));
        Assert.All(rows.Where(r => r.Prediction.Key.Metric == ForecastMetric.Mood), r => Assert.Equal(.1, r.Scalar!.Value, 10));
        Assert.All(rows, r => Assert.NotEqual(ForecastExecution.Consistent, r.Cohort));
        Assert.Same(comparison, selected.Selection);
        Assert.Contains(observations, o => o.UnknownReason == "food_stock_prediction_unavailable");
        Assert.NotNull(ForecastResidualReport.Build(new(predictions, observations, [])));
    }

    [Fact]
    public void CompletionUsesOnlyPositivelyUnfinishedReadNotAdmissionOrNativeStateTick()
    {
        var predictions = new List<ForecastPrediction>(); var observations = new List<ForecastObservation>();
        var owner = new ForecastEvidencePending(predictions.Add, observations.Add);
        owner.Review(Scope, 100, Needs(100), Context(100));
        owner.Capture(Scope, 100, 1, Selection(), Context(100), ForecastAdmission.Admitted, [Batch], "method");
        owner.Tasks(Scope, Batch, Read(120, "started"));
        owner.Tasks(Scope, Batch, Read(110, "started")); // stale task read cannot move the lower bound backwards
        owner.Tasks(Scope, Batch, Read(160, "completed"));
        owner.Tasks(Scope, Batch, Read(160, "completed"));
        var observation = Assert.Single(observations, o => o.Key.Metric == ForecastMetric.TaskCompletion);
        Assert.Equal(ForecastCompletion.Interval, observation.Completion);
        Assert.Equal(120, observation.LowerTick.Value); Assert.Equal(160, observation.UpperTick.Value);
        var residual = ForecastResiduals.Match(predictions.Single(p => p.Key.Metric == ForecastMetric.TaskCompletion), observation);
        Assert.Null(residual.Scalar); Assert.Equal(-30, residual.Lower); Assert.Equal(10, residual.Upper);
    }

    [Theory]
    [InlineData("failed", "method", ForecastExecution.Interrupted)]
    [InlineData("started", "other", ForecastExecution.Replaced)]
    public void ObservedDivergenceRemainsSeparateFromScalarAccuracy(string state, string method, ForecastExecution expected)
    {
        var predictions = new List<ForecastPrediction>(); var observations = new List<ForecastObservation>();
        var owner = new ForecastEvidencePending(predictions.Add, observations.Add);
        owner.Review(Scope, 100, Needs(100), Context(100));
        owner.Capture(Scope, 100, 1, Selection(), Context(100), ForecastAdmission.Admitted, [Batch], "method");
        owner.Tasks(Scope, Batch, Read(120, "started")); owner.Tasks(Scope, Batch, Read(130, state, method));
        owner.Review(Scope, 200, Needs(200), Context(200));
        Assert.Equal(expected, observations.Single(o => o.Key.Metric == ForecastMetric.FoodLevel).Execution);
    }

    [Fact]
    public void RefusalDoesNotClaimExecutionAndUnchangedContinuationDoesNotInvalidateEarlierReview()
    {
        var predictions = new List<ForecastPrediction>(); var observations = new List<ForecastObservation>();
        var owner = new ForecastEvidencePending(predictions.Add, observations.Add);
        owner.Review(Scope, 100, Needs(100), Context(100));
        owner.Capture(Scope, 100, 1, Selection(), Context(100), ForecastAdmission.Refused, [Batch], "method");
        owner.Capture(Scope, 100, 2, Selection(), Context(100), ForecastAdmission.Continued, [Batch], "method");
        owner.Review(Scope, 201, Needs(201), Context(201));
        var rows = ForecastResiduals.Compare(predictions, observations).Where(r => r.Prediction.Key.Metric == ForecastMetric.FoodLevel).ToArray();
        Assert.Equal(ForecastExecution.NotExecuted, rows[0].Cohort);
        Assert.Equal(ForecastExecution.Unknown, rows[1].Cohort);
        Assert.All(rows, r => { Assert.Equal("tick_mismatch", r.Reason); Assert.Null(r.Scalar); });
    }

    [Theory]
    [InlineData(true, false, "world_changed")]
    [InlineData(false, true, "world_changed")]
    [InlineData(false, false, "tick_rewind")]
    public void LoadsMapsAndRewindsRetireOldScope(bool load, bool map, string reason)
    {
        var observations = new List<ForecastObservation>(); var owner = new ForecastEvidencePending(_ => { }, observations.Add);
        owner.Review(Scope, 100, Needs(100), Context(100));
        owner.Capture(Scope, 100, 1, Selection(), Context(100), ForecastAdmission.Admitted, [Batch], "method");
        var next = load ? Scope with { Load = new("next") } : map ? Scope with { Map = new(2) } : Scope;
        owner.Review(next, load || map ? 100 : 99, null, null);
        Assert.Equal(0, owner.Count); Assert.Contains(observations, o => o.UnknownReason == reason);
        owner.Tasks(Scope, Batch, Read(150, "completed")); Assert.DoesNotContain(observations, o => o.Completion == ForecastCompletion.Interval);
    }

    [Fact]
    public void CapacityExpiryAndMissingReadsRemainVisibleAndBounded()
    {
        var observations = new List<ForecastObservation>(); var owner = new ForecastEvidencePending(_ => { }, observations.Add);
        owner.Review(Scope, 100, Needs(100), Context(100));
        for (ulong i = 1; i <= 1100; i++) owner.Capture(Scope, 100, i, Selection(), Context(100), ForecastAdmission.Admitted, [Batch], "method");
        Assert.Equal(ForecastEvidencePending.Capacity, owner.Count);
        Assert.Contains(observations, o => o.UnknownReason == "pending_capacity");
        owner.Review(Scope, 201, null, null);
        Assert.Contains(observations, o => o.UnknownReason == "fresh_needs_unavailable");
        owner.Review(Scope, 201 + ForecastEvidencePending.ExpiryTicks, null, null);
        Assert.Equal(0, owner.Count); Assert.Contains(observations, o => o.UnknownReason == "pending_expired");
    }
    [Fact]
    public void FirstCompletedReadDoesNotInventAnAdmissionBasedLowerBound()
    {
        var observations = new List<ForecastObservation>(); var owner = new ForecastEvidencePending(_ => { }, observations.Add);
        owner.Review(Scope, 100, Needs(100), Context(100));
        owner.Capture(Scope, 100, 1, Selection(), Context(100), ForecastAdmission.Admitted, [Batch], "method");
        owner.Tasks(Scope, Batch, Read(160, "completed"));
        var row = Assert.Single(observations, o => o.Key.Metric == ForecastMetric.TaskCompletion);
        Assert.Equal("completion_lower_bound_unavailable", row.UnknownReason);
        Assert.False(row.LowerTick.IsKnown); Assert.Equal(ForecastCompletion.Unknown, row.Completion);
    }

    [Fact]
    public void ShelterMatchingRequiresSameGeometryAndActualCompleteTemperatureCoverage()
    {
        var room = new ForecastShelterId(ForecastShelterSource.Observed, "room");
        var geometry = new ForecastShelterPlan(room, [new(1, 1), new(1, 2)], [], Known(20d), default, default, [], []);
        var context = Context(100) with { Shelters = [geometry] };
        var selected = Selection();
        selected = selected with { Selected = selected.Selected with { Shelters = [new(room, Known(0d), Known(21d), [], [])] } };
        var prediction = ForecastLiveEvidence.Predictions("run", "load", "1", "review", 100, selected, context,
            ForecastAdmission.Admitted, [Batch], "method").Single(p => p.Key.Metric == ForecastMetric.ShelterTemperature);
        ThermalCell Cell(int z) => new(new(1, z), Known(23d), "roof", false, false, "", false, false, 0, false);
        var observed = context with { Needs = Needs(200), Thermal = new() { Cells = [Cell(1), Cell(2)] } };
        Assert.Equal(23, ForecastLiveEvidence.Observe(prediction, 200, Needs(200), observed, ForecastExecution.Unknown).Value.Value);
        var partial = ForecastLiveEvidence.Observe(prediction, 200, Needs(200), observed with { Thermal = new() { Cells = [Cell(1)] } }, ForecastExecution.Unknown);
        Assert.Equal("temperature_coverage_incomplete", partial.UnknownReason); Assert.False(partial.Value.IsKnown);
        var changed = ForecastLiveEvidence.Observe(prediction, 200, Needs(200), observed with { Shelters = [geometry with { Interior = [new(9, 9)] }] }, ForecastExecution.Unknown);
        Assert.Equal("shelter_geometry_unobserved", changed.UnknownReason); Assert.False(changed.Value.IsKnown);
    }
    [Fact]
    public void OldCompletedRevisionCannotFinishCurrentTaskAndChangedRevisionCannotClaimConsistency()
    {
        var observations = new List<ForecastObservation>(); var owner = new ForecastEvidencePending(_ => { }, observations.Add);
        owner.Review(Scope, 100, Needs(100), Context(100));
        owner.Capture(Scope, 100, 1, Selection(), Context(100), ForecastAdmission.Admitted, [Batch], "method");
        owner.Tasks(Scope, Batch, Read(115, "failed", revision: 0));
        owner.Tasks(Scope, Batch, Read(120, "completed", revision: 0));
        Assert.DoesNotContain(observations, o => o.Key.Metric == ForecastMetric.TaskCompletion);
        var revised = Batch with { Orders = [Batch.Orders[0] with { Revision = 2 }] };
        owner.Tasks(Scope, revised, Read(130, "started", revision: 2));
        owner.Tasks(Scope, revised, Read(160, "completed", revision: 2));
        var row = Assert.Single(observations, o => o.Key.Metric == ForecastMetric.TaskCompletion);
        Assert.Equal(ForecastCompletion.Interval, row.Completion);
        Assert.Equal(ForecastExecution.Unknown, row.Execution);
    }

    [Fact]
    public void RewindCannotReuseCorrelationIdentityEvenWhenReviewAndTickRepeat()
    {
        var predictions = new List<ForecastPrediction>(); var observations = new List<ForecastObservation>();
        var owner = new ForecastEvidencePending(predictions.Add, observations.Add);
        owner.Review(Scope, 100, Needs(100), Context(100));
        owner.Capture(Scope, 100, 1, Selection(), Context(100), ForecastAdmission.Admitted, [Batch], "method");
        owner.Review(Scope, 99, Needs(99), Context(99));
        owner.Review(Scope, 100, Needs(100), Context(100));
        owner.Capture(Scope, 100, 1, Selection(), Context(100), ForecastAdmission.Admitted, [Batch], "method");
        owner.Review(Scope, 200, Needs(200), Context(200));
        var pairs = ForecastResiduals.Compare(predictions, observations).Where(r => r.Prediction.Key.Metric == ForecastMetric.FoodLevel).ToArray();
        Assert.Equal(2, pairs.Length); Assert.NotEqual(pairs[0].Prediction.Key, pairs[1].Prediction.Key);
        Assert.Equal("tick_rewind", pairs[0].Observation!.UnknownReason); Assert.Null(pairs[0].Scalar);
        Assert.Equal(.2, pairs[1].Scalar!.Value, 10);
    }
}

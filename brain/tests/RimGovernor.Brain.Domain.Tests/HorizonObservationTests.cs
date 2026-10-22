using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.Acceptance.Cases;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class HorizonObservationTests
{
    [Fact]
    public void OrdinaryStagedCaseCannotBorrowTheHorizonBudget()
    {
        var test = ForecastResidualCase.Case() with { ObservesHorizon = null };
        Assert.Throws<AcceptanceException>(test.Validate);
    }

    [Fact]
    public void DeclaredHorizonUsesProductionDefinitionAndForbidsBodyFixtures()
    {
        var test = ForecastResidualCase.Case();
        test.Validate();
        Assert.Equal((int)(ForecastCoupled.TierOneHorizonDays * GameTime.TicksPerDay), test.ObservesHorizon!.HorizonTicks);
        Assert.Equal(test.Ticks, test.Cap);
        Assert.False(test.Declares(FixtureStage.Tool));
        Assert.Throws<AcceptanceException>((test with { Ops = [FixtureStage.Tool] }).Validate);
        Assert.Throws<AcceptanceException>((test with { Stage = null }).Validate);
        Assert.Throws<AcceptanceException>((test with { Serve = null }).Validate);
        Assert.Throws<AcceptanceException>((test with { Ticks = test.Ticks + 1 }).Validate);
    }

    [Fact]
    public void MissingInvalidOrUnjustifiedDeclarationsAreRejected()
    {
        var test = ForecastResidualCase.Case();
        var horizon = test.ObservesHorizon!;
        foreach (var invalid in new[]
        {
            horizon with { Horizon = ProductionObservationHorizon.Unspecified },
            horizon with { Horizon = (ProductionObservationHorizon)99 },
            horizon with { ClaimReason = " " },
            horizon with { ObservationSlackTicks = 0 },
            horizon with { ObservationSlackTicks = -1 },
            horizon with { ObservationSlackTicks = int.MaxValue },
            horizon with { ObservationSlackReason = " " },
        })
        {
            Assert.Throws<AcceptanceException>((test with { ObservesHorizon = invalid }).Validate);
        }
    }

    [Fact]
    public void NativeClaimNeedsObservedValuesAndCompletedTaskNotJustPredictions()
    {
        ForecastResidual Row(ForecastMetric metric)
        {
            var key = new ForecastEvidenceKey("run", "load", "map", "review", metric.ToString(), metric, "pawn", "scope", "level", "method", "task", 1);
            var prediction = new ForecastPrediction(key, 0, 60000, Fact.Known(metric == ForecastMetric.TaskCompletion ? 100d : .5), "", ForecastAdmission.Admitted);
            var observation = new ForecastObservation(key, 60100, Fact.Known(.6), "", ForecastExecution.Unknown,
                metric == ForecastMetric.TaskCompletion ? ForecastCompletion.Interval : ForecastCompletion.NotApplicable,
                Fact.Known(90d), Fact.Known(120d), metric == ForecastMetric.TaskCompletion ? "native_task_read" : "rounds_native_observation");
            return ForecastResiduals.Match(prediction, observation);
        }
        var rows = new[] { ForecastMetric.FoodStock, ForecastMetric.FoodLevel, ForecastMetric.RestLevel, ForecastMetric.Mood, ForecastMetric.TaskCompletion }.Select(Row).ToArray();
        Assert.True(ForecastResidualCase.Complete(rows));
        Assert.False(ForecastResidualCase.Complete(rows.Take(3).ToArray()));
        Assert.False(ForecastResidualCase.Complete(rows.Select(r => r with { Observation = null }).ToArray()));
        Assert.False(ForecastResidualCase.Complete(rows.Select(r => r.Prediction.Key.Metric == ForecastMetric.Mood ? r with { Reason = "unknown_observation" } : r).ToArray()));
    }
}

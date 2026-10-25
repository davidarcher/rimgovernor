using RimGovernor.Brain.Facts;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Executor;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using Xunit;
using StoreReview = RimGovernor.Brain.Store;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class RecordedReviewInputsTests
{
    [Fact]
    public void FieldBudgetAndObservedGeometrySurviveTheReviewedRecording()
    {
        var observed = ColonyFactsCoreTests.Reading();
        var window = new Held<PlanningCells>
        {
            AsOf = observed.Identity.Tick.Value,
            Complete = true,
            Source = "test.planning",
            Value = new() { Region = new(2, 3, 4, 4), Cells = [SiteCellFixtures.At(new(3, 4))] },
        };
        observed = observed with
        {
            Window = window,
            Facts = observed.Facts with
            {
                Census = observed.Facts.Census with { ColonistCount = 1 },
                FieldCoverage = 0,
                FieldCrops = [new() { Edible = true, GrowingCells = 30, Demand = 2, GrowDays = 10, HarvestNutrition = 1 }],
            },
        };
        var review = new RoundsReading { Reading = observed, Frame = new(), Emergency = new() };
        review = review.WithFacts(f => ColonyPlanningRules.ApplyFieldBudget(f, 5));
        var result = new StoreReview.RoundsResult
        {
            Detection = new(review.Reading.Facts, new(), new()),
        };

        var recorded = Assert.IsType<SnapshotRounds>(SnapshotRoundsReplay.FromReview(default, observed.Identity.Tick, result, observed));

        Assert.Equal(.5, recorded.Facts.FieldCoverage);
        Assert.Same(review.Reading.Facts, recorded.Facts);
        Assert.Same(recorded.Reading.Facts, recorded.Facts);
        Assert.Same(observed.Snapshot, recorded.Reading.Snapshot);
        Assert.Same(observed.Tables, recorded.Reading.Tables);
        Assert.Same(window, recorded.Reading.Window);
        Assert.Single(recorded.Reading.PlanningWindow.Cells);

        recorded.TrimCells();
        Assert.Empty(recorded.Reading.PlanningWindow.Cells);
        Assert.Equal(window.Value.Region, recorded.Reading.PlanningWindow.Region);
        Assert.Equal(window.AsOf, Assert.IsType<Held<PlanningCells>>(recorded.Reading.Window).AsOf);
        Assert.Single(window.Value.Cells);
        Assert.Equal(.5, recorded.Facts.FieldCoverage);
    }
}

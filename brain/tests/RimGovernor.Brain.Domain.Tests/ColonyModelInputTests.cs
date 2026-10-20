using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

// #3523: the tier-2 colony model's input, assembled from the review frame. Nothing runs the model yet.
public sealed class ColonyModelInputTests
{
    [Fact]
    public void TheReviewFrameYieldsAValidInput()
    {
        var (input, unknowns) = RoundsColonyModel.InputOf(ColonyModelFixtures.Projection());

        Assert.Empty(unknowns);
        Assert.True(input.Valid());
        Assert.Equal(12, input.Climate.Value.Temperatures.Count);
        Assert.Equal(4, input.Climate.Value.CurrentTwelfth);
        Assert.Equal(40, input.Calendar.Value.GrowingDays);
        var field = Assert.Single(input.Land.Value);
        Assert.Equal(("zone1", ColonyModelFixtures.Crop, false), (field.Id, field.Crop, field.Planned));
        Assert.Equal([0.5, 0.6, 0.7, 0.8], field.Fertility.Select(f => Math.Round(f, 6)));
        Assert.Equal(12.5, input.Stocks.Nutrition.Value);
        Assert.Equal([new Amount(new Resource("MedicineHerbal"), 4)], input.Stocks.Medicine.Value);
        Assert.Equal(["DevilstrandCloth", "Steel"], input.Stocks.Materials.Value.Select(a => a.Resource.Value));
        Assert.Equal(ColonyModelFixtures.Project, Assert.Single(input.Research.Value.Projects).Project.Value);
        Assert.Empty(input.Sites.Value);
        Assert.Empty(input.PlannedSites.Value);
    }

    [Fact]
    public void AFrameMissingTheClimateHasAnUnknownClimateAndNamesIt()
    {
        var projection = ColonyModelFixtures.Projection();
        projection = projection with { Facts = projection.Facts with { Gear = Unknown<GearObservation>() } };

        var (input, unknowns) = RoundsColonyModel.InputOf(projection);

        Assert.False(input.Climate.IsKnown);
        Assert.False(input.Valid());
        Assert.StartsWith("climate:", Assert.Single(unknowns));
    }

    [Fact]
    public void TheCalendarAndEveryOtherUnreadSectionAreNamedNotDefaulted()
    {
        var (input, unknowns) = RoundsColonyModel.InputOf(new ColonyProjection());

        Assert.False(input.Valid());
        foreach (var section in new[] { "climate:", "calendar:", "crops:", "research:", "stocks:", "pawns:", "recipes:", "sites:", "wildlife:" })
        {
            Assert.Contains(unknowns, u => u.StartsWith(section, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void TheDevilstrandFixtureMatchesTheDesignNotesTable()
    {
        var input = ColonyModelFixtures.Devilstrand();

        Assert.True(input.Valid());
        var project = Assert.Single(input.Research.Value.Projects);
        Assert.Equal(800, project.Cost);
        Assert.Equal([ColonyModelFixtures.Prerequisite], project.Prerequisites.Select(p => p.Value));
        var crop = Assert.Single(input.Crops.Value);
        Assert.Equal(10, crop.SowMinSkill.Value);
        Assert.Equal(400, crop.SowWork.Value);
        Assert.Equal(0.5, crop.FertilityMin.Value);
        Assert.Equal(22.5, crop.GrowDays.Value);
        Assert.Equal(6, crop.UnitsPerCell.Value);
        // 22.5 growing days at the 0.55 of a day a plant grows are about 41 calendar days.
        Assert.Equal(41, Math.Round(crop.GrowDays.Value / (1 - crop.RestingDayFraction.Value)));
        var recipe = Assert.Single(input.Recipes.Value);
        Assert.Equal(10_000, recipe.WorkAmount.Value);
        Assert.Equal(80, Assert.Single(Assert.Single(recipe.Slots.Value)).Count);
        var skills = Assert.Single(input.Pawns.Value).Skills.Value;
        Assert.Equal(10, Assert.Single(skills).Level);
    }

    [Fact]
    public void TheCropRowCarriesTheGrowthTemperatureBandAndTheRestingFraction()
    {
        var crop = Assert.Single(RoundsFieldPlanner.CropChoices(ColonyModelFixtures.Projection()));

        Assert.Equal([0.0, 10, 42, 58], new[] { crop.MinGrowthTemperature.Value, crop.MinOptimalGrowthTemperature.Value, crop.MaxOptimalGrowthTemperature.Value, crop.MaxGrowthTemperature.Value });
        Assert.Equal(0.45, crop.RestingDayFraction.Value, 9);
    }

    [Fact]
    public void ACropWithAnUnorderedBandIsNamedInTheUnknowns()
    {
        var projection = ColonyModelFixtures.Projection();
        projection = projection with { Definitions = [ColonyModelFixtures.CropRow() with { MinOptimalGrowthTemperature = Known(60.0) }] };

        var (_, unknowns) = RoundsColonyModel.InputOf(projection);

        Assert.Equal($"crops: {ColonyModelFixtures.Crop} lacks growth temperature band", Assert.Single(unknowns));
    }

    [Fact]
    public void APlannedFieldTakesTheLayoutPlansFieldCellsOffTheStandingZones()
    {
        var projection = ColonyModelFixtures.Projection();
        var plan = new LayoutPlan { Zones = [new LayoutZone { Kind = LayoutPlanRules.ZoneField, Runs = [new RowRun(0, 2, 4)] }] };
        projection = projection with
        {
            LayoutPlan = Known(plan),
            Cells = [.. projection.Cells, new SiteCell { Cell = new Cell(4, 0), Fertility = Known(1.1) }, new SiteCell { Cell = new Cell(5, 0), Fertility = Known(0.9) }],
        };

        var planned = Assert.Single(RoundsColonyModel.InputOf(projection).Input.Land.Value, f => f.Planned);

        // Cells 2 and 3 already belong to the standing zone.
        Assert.Equal(("plan-0", ""), (planned.Id, planned.Crop));
        Assert.Equal([1.1, 0.9], planned.Fertility);
    }

    [Fact]
    public void ACellWithoutAFertilityReadingMakesTheLandUnknown()
    {
        var projection = ColonyModelFixtures.Projection();
        var plan = new LayoutPlan { Zones = [new LayoutZone { Kind = LayoutPlanRules.ZoneField, Runs = [new RowRun(0, 4, 2)] }] };
        projection = projection with { LayoutPlan = Known(plan), Cells = [.. projection.Cells, new SiteCell { Cell = new Cell(4, 0), Fertility = Known(1.1) }] };

        var (input, unknowns) = RoundsColonyModel.InputOf(projection);

        Assert.False(input.Land.IsKnown);
        Assert.Contains(unknowns, u => u.StartsWith("land:", StringComparison.Ordinal));
    }

    private static ColonyProjection WithWorkRead(ColonyProjection projection, double[] benchFactors, double researchSpeed, double plantWorkSpeed)
    {
        var review = projection.Facts.NeedsAlternatives!;
        var pawn = review.Observation.Pawns[0] with { ResearchSpeed = Known(researchSpeed), PlantWorkSpeed = Known(plantWorkSpeed) };
        var observation = review.Observation with { Pawns = [pawn], ResearchBenchFactors = EquatableArray.From(benchFactors) };
        return projection with { Facts = projection.Facts with { NeedsAlternatives = review with { Observation = observation } } };
    }

    [Fact]
    public void TheResearchBenchesAreTheBenchesTheNeedsReadCountsWhateverTheCurrentProjectIs()
    {
        var input = RoundsColonyModel.InputOf(WithWorkRead(ColonyModelFixtures.Projection(), [1.0, 1.5], 0.8, 0.9)).Input;

        Assert.Equal(2, input.Research.Value.Benches.Value);
        var pawn = input.Pawns.Value[0];
        // The researcher takes the best bench: the pawn's stat times the best factor.
        Assert.Equal(1.2, pawn.ResearchSpeed.Value, 9);
        Assert.Equal(0.9, pawn.PlantWorkSpeed.Value, 9);
    }

    [Fact]
    public void ABenchlessColonyHasZeroBenchesAndNoResearchSpeed()
    {
        var input = RoundsColonyModel.InputOf(WithWorkRead(ColonyModelFixtures.Projection(), [], 0.8, 1.2)).Input;

        Assert.Equal(0, input.Research.Value.Benches.Value);
        Assert.False(input.Pawns.Value[0].ResearchSpeed.IsKnown);
    }

    [Fact]
    public void ARestingPawnWithNoBedStillHasARestGainSoItsCycleIsADay()
    {
        var projection = ColonyModelFixtures.Projection();
        var review = projection.Facts.NeedsAlternatives!;
        var pawn = review.Observation.Pawns[0];
        // No bed and no recreation target; the rest need carries the ground recovery the game applies to a pawn lying down without a bed.
        pawn = pawn with
        {
            Targets = [.. pawn.Targets.Where(t => t.Kind == "food")],
            Needs = EquatableArray.From(pawn.Needs.Select(n => n.Kind == "rest" ? n with { UnservedGainPerTick = Known(0.00002) } : n)),
        };
        projection = projection with { Facts = projection.Facts with { NeedsAlternatives = review with { Observation = review.Observation with { Pawns = [pawn] } } } };

        var (input, unknowns) = RoundsColonyModel.InputOf(projection);

        var needs = input.Pawns.Value[0].Needs;
        Assert.Equal(0.00002, needs.Single(n => n.Kind == "rest").Gain.Value);
        Assert.False(needs.Single(n => n.Kind == "joy").Gain.IsKnown);
        Assert.DoesNotContain(unknowns, u => u.Contains("rest_gain_unknown", StringComparison.Ordinal));
        Assert.Contains(unknowns, u => u.Contains("joy_gain_unknown", StringComparison.Ordinal));
    }
}

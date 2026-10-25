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
        var input = ColonyModelFixtures.ReadyOf(ColonyModelFixtures.Projection());

        Assert.True(input.Valid());
        Assert.Empty(input.Gaps());
        Assert.Equal(12, input.Climate.Temperatures.Count);
        Assert.Equal(4, input.Climate.CurrentTwelfth);
        Assert.Equal(40, input.Calendar.GrowingDays);
        var field = Assert.Single(input.Land);
        Assert.Equal(("zone1", ColonyModelFixtures.Crop, false), (field.Id, field.Crop, field.Planned));
        Assert.Equal([0.5, 0.6, 0.7, 0.8], field.Fertility.Select(f => Math.Round(f, 6)));
        Assert.Equal(12.5, input.Stocks.Nutrition);
        Assert.Equal([new Amount(new Resource("MedicineHerbal"), 4)], input.Stocks.Medicine);
        Assert.Equal(["DevilstrandCloth", "Steel"], input.Stocks.Materials.Select(a => a.Resource.Value));
        Assert.Equal(ColonyModelFixtures.Project, Assert.Single(input.Research.Projects).Project.Value);
    }

    [Fact]
    public void AFrameMissingTheClimateIsUnreadyAndNamesIt()
    {
        var projection = ColonyModelFixtures.Projection();
        projection = projection with { Facts = projection.Facts with { Gear = Unknown<GearObservation>() } };

        var read = RoundsColonyModel.InputOf(projection);

        var unready = Assert.IsType<ColonyModelInputRead.Unready>(read);
        Assert.StartsWith("climate:", Assert.Single(unready.Gaps));
    }

    [Fact]
    public void TheCalendarAndEveryOtherUnreadSectionAreNamedNotDefaulted()
    {
        var unknowns = ColonyModelFixtures.GapsOf(ColonyFactsCoreTests.Projection());

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
        var project = Assert.Single(input.Research.Projects);
        Assert.Equal(800, project.Cost);
        Assert.Equal([ColonyModelFixtures.Prerequisite], project.Prerequisites.Select(p => p.Value));
        var crop = Assert.Single(input.Crops);
        Assert.Equal(10, crop.SowMinSkill.Value);
        Assert.Equal(400, crop.SowWork.Value);
        Assert.Equal(0.5, crop.FertilityMin.Value);
        Assert.Equal(22.5, crop.GrowDays.Value);
        Assert.Equal(6, crop.UnitsPerCell.Value);
        // 22.5 growing days at the 0.55 of a day a plant grows are about 41 calendar days.
        Assert.Equal(41, Math.Round(crop.GrowDays.Value / (1 - crop.RestingDayFraction.Value)));
        var recipe = Assert.Single(input.Recipes);
        Assert.Equal(10_000, recipe.WorkAmount);
        Assert.Equal(80, Assert.Single(Assert.Single(recipe.Slots)).Count);
        var skills = Assert.Single(input.Pawns).Skills;
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
    public void ACropWithAnUnorderedBandLeavesTheInputUnreadyAndIsNamed()
    {
        var projection = ColonyModelFixtures.Projection();
        projection = projection with { Definitions = [ColonyModelFixtures.CropRow() with { MinOptimalGrowthTemperature = Known(60.0) }] };

        var unknowns = ColonyModelFixtures.GapsOf(projection);

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

        var planned = Assert.Single(ColonyModelFixtures.ReadyOf(projection).Land, f => f.Planned);

        // Cells 2 and 3 already belong to the standing zone.
        Assert.Equal(("plan-0", ""), (planned.Id, planned.Crop));
        Assert.Equal([1.1, 0.9], planned.Fertility);
    }

    [Fact]
    public void ACellWithoutAFertilityReadingLeavesTheInputUnready()
    {
        var projection = ColonyModelFixtures.Projection();
        var plan = new LayoutPlan { Zones = [new LayoutZone { Kind = LayoutPlanRules.ZoneField, Runs = [new RowRun(0, 4, 2)] }] };
        projection = projection with { LayoutPlan = Known(plan), Cells = [.. projection.Cells, new SiteCell { Cell = new Cell(4, 0), Fertility = Known(1.1) }] };

        var unknowns = ColonyModelFixtures.GapsOf(projection);

        Assert.Contains(unknowns, u => u.StartsWith("land:", StringComparison.Ordinal));
    }

    private static ColonyProjection WithWorkRead(ColonyProjection projection, double[] benchFactors, double researchSpeed, double plantWorkSpeed)
    {
        var review = projection.Facts.NeedsAlternatives!;
        var pawn = review.Observation.Pawns[0] with { ResearchSpeed = researchSpeed, PlantWorkSpeed = plantWorkSpeed };
        var observation = review.Observation with { Pawns = [pawn], ResearchBenches = EquatableArray.From(benchFactors.Select((factor, i) => new RimGovernor.Contracts.Wire.Observations.ResearchBenchRead("bench" + i, "SimpleResearchBench", new(i, 0), factor))) };
        return projection with { Facts = projection.Facts with { NeedsAlternatives = review with { Observation = observation } } };
    }

    [Fact]
    public void TheResearchBenchesAreTheBenchesTheNeedsReadCountsWhateverTheCurrentProjectIs()
    {
        var input = ColonyModelFixtures.ReadyOf(WithWorkRead(ColonyModelFixtures.Projection(), [1.0, 1.5], 0.8, 0.9));

        Assert.Equal(2, input.Research.Benches);
        var pawn = input.Pawns[0];
        // The researcher takes the best bench: the pawn's stat times the best factor.
        Assert.Equal(1.2, pawn.ResearchSpeed, 9);
        Assert.Equal(0.9, pawn.PlantWorkSpeed, 9);
    }

    [Fact]
    public void ABenchlessColonyHasZeroBenchesAndZeroResearchSpeed()
    {
        var input = ColonyModelFixtures.ReadyOf(WithWorkRead(ColonyModelFixtures.Projection(), [], 0.8, 1.2));

        Assert.Equal(0, input.Research.Benches);
        Assert.Equal(0, input.Pawns[0].ResearchSpeed);
    }

    [Fact]
    public void APawnWithNoBedAndNoRecreationBuildingStillRecoversSoItsCycleIsADay()
    {
        // No bed and no recreation target: the rest need carries the ground recovery the game applies to a pawn lying down
        // without a bed, the joy need the walk a pawn takes without a building (#3825).
        var input = ColonyModelFixtures.ReadyOf(NoBedNoRecreation(ColonyModelFixtures.Projection(), Walk));

        var needs = input.Pawns[0].Needs;
        Assert.Equal(0.00002, needs.Single(n => n.Kind == "rest").Gain);
        Assert.Equal(Walk, needs.Single(n => n.Kind == "joy").Gain);
        Assert.IsType<ModelService.NoneObserved>(needs.Single(n => n.Kind == "joy").Service);
        Assert.IsType<ModelService.NoneObserved>(needs.Single(n => n.Kind == "rest").Service);
        Assert.True(input.Valid());
    }

    [Fact]
    public void ABuiltTargetIsAnObservedServiceNotAnAbsentOne()
    {
        var input = ColonyModelFixtures.Devilstrand();

        Assert.IsType<ModelService.Observed>(input.Pawns[0].Needs.Single(n => n.Kind == "food").Service);
    }

    [Fact]
    public void AJoyReadWithoutItsOutdoorSharesThrows()
    {
        // #3825 D7, P10: when a walk can be taken is a fact the read carries; a joy row without it is a missing read, not all weather.
        var projection = NoBedNoRecreation(ColonyModelFixtures.Projection(), Walk);
        var review = projection.Facts.NeedsAlternatives!;
        var pawn = review.Observation.Pawns[0];
        pawn = pawn with { Needs = EquatableArray.From(pawn.Needs.Select(n => n.Kind == "joy" ? n with { OutdoorShareByTwelfth = default } : n)) };
        projection = projection with { Facts = projection.Facts with { NeedsAlternatives = review with { Observation = review.Observation with { Pawns = [pawn] } } } };

        Assert.Throws<RequiredFactException>(() => RoundsColonyModel.InputOf(projection));
    }

    /// <summary>GoForWalk's gain a tick at tolerance factor 1: joyGainRate 1 × 0.36 / 2500 (JoyUtility.JoyTickCheckEnd).</summary>
    internal const double Walk = 0.36 / GameTime.TicksPerHour;

    /// <summary>The projection with its colonist's bed and recreation targets gone and the needs' unserved recoveries read.</summary>
    internal static ColonyProjection NoBedNoRecreation(ColonyProjection projection, double walk)
    {
        var review = projection.Facts.NeedsAlternatives!;
        var pawn = review.Observation.Pawns[0];
        pawn = pawn with
        {
            Targets = [.. pawn.Targets.Where(t => t.Kind == "food")],
            Needs = EquatableArray.From(pawn.Needs.Select(n => n.Kind switch
            {
                "rest" => n with { UnservedGainPerTick = 0.00002 },
                "joy" => n with { UnservedGainPerTick = walk },
                _ => n,
            })),
        };
        return projection with { Facts = projection.Facts with { NeedsAlternatives = review with { Observation = review.Observation with { Pawns = [pawn] } } } };
    }
}

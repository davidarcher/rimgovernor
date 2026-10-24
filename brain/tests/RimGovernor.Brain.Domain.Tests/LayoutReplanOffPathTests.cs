using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The layout replan off the review's hold path (#3565): its run reports the chain's outcome instead of throwing,
/// and a review adopts a finished run only over the world and the saved plan it started from.
/// </summary>
public sealed class LayoutReplanOffPathTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("root"), new(1), new(1));

    [Fact]
    public void ARunOverUnreadFactsReportsTheSkippedChainAndNoPlan()
    {
        var catalog = new DefinitionCatalog([new Verse.ThingDef { defName = "Silver" }, new Verse.TerrainDef { defName = "Soil" }], DefStatTable.Empty, new CatalogDerived("Silver", "Wort", 10f));

        var run = Rounder.RunLayoutReplan(new LayoutPlan(), new StationFacts { Catalog = new StationCatalog() }, new MapSurvey(), catalog, [], new RoomGrowth());

        Assert.False(run.Advised);
        Assert.Equal(RimGovernor.Brain.Telemetry.RequiredFactTelemetry.Kind, run.StationRow.Kind);
        Assert.Equal(RimGovernor.Brain.Telemetry.TelemetryKinds.LayoutStationsKind, run.StationRow.Target);
        Assert.Equal("skipped", run.StationRow.Verdict);
        Assert.Equal(RimGovernor.Brain.Telemetry.RequiredFactTelemetry.Reason, run.StationRow.Reason);
        Assert.Equal(RimGovernor.Brain.Telemetry.TelemetryLevel.Error, run.StationRow.Level);
        Assert.Null(run.Next);
        Assert.Null(run.Error);
    }

    [Fact]
    public void AFinishedReplanIsAdoptedOnlyOverTheWorldAndPlanItStartedFrom()
    {
        var held = new HeldLayout(new LayoutPlan(), new Tick(100));
        var pending = new PendingLayoutReplan(World, new Tick(120), held, new ColonyProjection(), new MapSurvey(), null!, new RoomGrowth(), 3, "", "terrain", [], new PerimeterWall(default, default), [],
            Task.FromResult(new LayoutReplanRun { StationRow = new RimGovernor.Brain.Telemetry.Decision { Kind = RimGovernor.Brain.Telemetry.TelemetryKinds.LayoutStationsKind } }));

        Assert.True(Rounder.LayoutReplanAdoptable(pending, World, new Tick(130), held, true));
        Assert.True(Rounder.LayoutReplanAdoptable(pending, World with { Revision = new(2), Native = new(3) }, new Tick(130), held, true));
        Assert.False(Rounder.LayoutReplanAdoptable(pending, World with { Load = new("reloaded") }, new Tick(130), held, true));
        Assert.False(Rounder.LayoutReplanAdoptable(pending, World, new Tick(130), held with { Tick = new Tick(125) }, true));
        Assert.False(Rounder.LayoutReplanAdoptable(pending, World, new Tick(110), held, true));
        Assert.False(Rounder.LayoutReplanAdoptable(pending, World, new Tick(130), held, false));
    }

    [Fact]
    public void TheHourlyCheckStartsNoReplanForConstructionWithinTheDayAndOneAfterReplanDaily()
    {
        // The last replan started at tick 1000; since then a wall went up and a tree was felled (#3739).
        var last = LayoutInputsChangedTests.Inputs(LayoutInputsChangedTests.Cells());
        var built = LayoutInputsChangedTests.Cells();
        built[0] = built[0] with { Built = true };
        built[1] = built[1] with { Tree = true, Built = true };
        var now = LayoutInputsChangedTests.Inputs(built);
        var replanned = new Tick(1000);

        Assert.Empty(LayoutPlanning.ReplanDrift(now, last, new Tick(1000 + LayoutPlanning.ReplanEvery.Value), replanned));
        Assert.Empty(LayoutPlanning.ReplanDrift(now, last, new Tick(1000 + LayoutPlanning.ReplanDaily.Value - 1), replanned));
        Assert.Equal(["daily"], LayoutPlanning.ReplanDrift(now, last, new Tick(1000 + LayoutPlanning.ReplanDaily.Value), replanned));
        // Structural drift replans within the day, named for what drifted.
        var mined = LayoutInputsChangedTests.Cells();
        mined[3] = mined[3] with { Rock = false, Walkable = true };
        Assert.Equal(["survey"], LayoutPlanning.ReplanDrift(LayoutInputsChangedTests.Inputs(mined), last, new Tick(1000 + LayoutPlanning.ReplanEvery.Value), replanned));
        // A rewind past the last replan replans.
        Assert.Equal(["daily"], LayoutPlanning.ReplanDrift(now, last, new Tick(500), replanned));
    }

    [Fact]
    public void ARoomAJournalPlanTookUpAfterTheRunStartedIsOneTheRunDidNotHold()
    {
        // The shell admitted in the review that started the run (#3537): room 5 is in flight now and the run held only
        // the rooms it had fixed, in flight, slept in or in use. Without a census every room was held.
        var growth = new RoomGrowth { Fixed = [1], InFlight = [2], Slept = [3], InUse = [4] };
        Assert.Equal([5], Rounder.NewlyHeldRooms(growth, [5, 4, 3, 2, 1]));
        Assert.Empty(Rounder.NewlyHeldRooms(growth with { Fixed = null }, [5]));
    }
}

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
        Assert.Equal("layout_stations", run.StationRow.Kind);
        Assert.Equal("skipped", run.StationRow.Verdict);
        Assert.Equal("facts_unknown", run.StationRow.Reason);
        Assert.Null(run.Next);
        Assert.Null(run.Error);
    }

    [Fact]
    public void AFinishedReplanIsAdoptedOnlyOverTheWorldAndPlanItStartedFrom()
    {
        var held = new HeldLayout(new LayoutPlan(), new Tick(100));
        var pending = new PendingLayoutReplan(World, new Tick(120), held, new ColonyProjection(), new MapSurvey(), null!, new RoomGrowth(), 3, "", "terrain", [], new PerimeterWall(default, default), [],
            Task.FromResult(new LayoutReplanRun { StationRow = new RimGovernor.Brain.Telemetry.Decision { Kind = "layout_stations" } }));

        Assert.True(Rounder.LayoutReplanAdoptable(pending, World, new Tick(130), held, true));
        Assert.True(Rounder.LayoutReplanAdoptable(pending, World with { Revision = new(2), Native = new(3) }, new Tick(130), held, true));
        Assert.False(Rounder.LayoutReplanAdoptable(pending, World with { Load = new("reloaded") }, new Tick(130), held, true));
        Assert.False(Rounder.LayoutReplanAdoptable(pending, World, new Tick(130), held with { Tick = new Tick(125) }, true));
        Assert.False(Rounder.LayoutReplanAdoptable(pending, World, new Tick(110), held, true));
        Assert.False(Rounder.LayoutReplanAdoptable(pending, World, new Tick(130), held, false));
    }
}

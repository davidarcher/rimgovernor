using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class LayoutFieldPlanningTests
{
    [Fact]
    public void DeficitOrderIsStableAndOnlyLeadOptionDeterminesWeight()
    {
        var crop = new CropChoice { Name = "rice" };
        IReadOnlyList<IReadOnlyList<FieldBlockOption>> ledger =
            [[new(crop, 2), new(crop, 99)], [new(crop, 5)], [new(crop, 5)], []];
        Assert.Equal([1, 2, 0, 3], LayoutFieldPlanning.RankFieldShortfalls(ledger));
    }

    [Fact]
    public void CropOptionsKeepSelectedFirstAndExcludeDuplicateIndoorAndBuildingCandidates()
    {
        SiteTypeCandidate Candidate(string name, SiteKind kind = SiteKind.Outdoor, int cells = 4) =>
            new() { Crop = new() { Name = name }, Kind = kind, Cells = cells, Needed = cells };
        var selected = Candidate("rice");
        var options = LayoutFieldPlanning.FieldBlockOptions(selected,
            [selected, Candidate("corn"), Candidate("corn"), Candidate("indoor", SiteKind.DarkRoom), Candidate("empty", cells: 0), Candidate("built") with { Buildings = [new()] }]);
        Assert.Equal(["rice", "corn"], options.Select(o => o.Crop.Name));
        Assert.True(LayoutFieldPlanning.AdoptsZone(new() { Name = "rice", Edible = true }, "corn", []));
        Assert.False(LayoutFieldPlanning.AdoptsZone(new() { Name = "rice", Edible = true }, "hay", ["hay"]));
        Assert.False(LayoutFieldPlanning.AdoptsZone(new() { Name = "hay" }, "corn", []));
    }

    [Fact]
    public void GrowingUsesConnectedRowMajorRingsAndNewBlocksPreferLargestComponent()
    {
        HashSet<Cell> zone = [new(0, 0)];
        HashSet<Cell> free = [new(0, -1), new(-1, 0), new(1, 0), new(0, 1), new(0, -2)];
        Assert.Equal([new Cell(0, -1), new Cell(-1, 0), new Cell(1, 0)], LayoutFieldPlanning.ConnectedAdds(zone, free, 3));
        HashSet<Cell> disconnected = [new(0, 0), new(10, 0), new(11, 0), new(10, 1), new(11, 1)];
        var picked = LayoutFieldPlanning.ConnectedPick(disconnected, [], [], new(0, 0), 4);
        Assert.Equal(4, picked.Count);
        Assert.DoesNotContain(new Cell(0, 0), picked);
        Assert.Equal(picked.Count, LayoutFieldPlanning.CellComponent([.. picked], picked[0]).Count);
    }

    [Fact]
    public void FieldPlacementDoesNotRequireWindowBeforeAFieldBlockIsOwed()
    {
        var facts = ColonyFactsCoreTests.Reading().Facts;
        Assert.Equal("no layout plan", LayoutFieldPlanning.PlanFieldBlock(facts, null, null, default, [], []).Reason);
        facts = facts with { LayoutPlan = new() };
        Assert.Equal("layout plan has no field blocks", LayoutFieldPlanning.PlanFieldBlock(facts, null, null, default, [], []).Reason);
        facts = facts with { LayoutPlan = new() { Zones = [new() { Kind = LayoutZoneKind.Field, Runs = [new(0, 0, 4)] }] } };
        Assert.Equal("no field cells needed", LayoutFieldPlanning.PlanFieldBlock(facts, null, null, default, [], []).Reason);
        Assert.Throws<RequiredFactException>(() => LayoutFieldPlanning.PlanFieldBlock(facts, null, null, default, [new(new() { Name = "rice" }, 1)], []));
    }

    [Fact]
    public void ShrinkSkipsBridgeCellsAndNeverDropsTheLastCell()
    {
        HashSet<Cell> zone = [new(0, 0), new(1, 0), new(2, 0)];
        var shrink = new FieldShrink("zone", "rice", zone, [new(1, 0), new(0, 0), new(2, 0)], 9);
        Assert.Equal([new Cell(0, 0), new Cell(2, 0)], LayoutFieldShrink.ShrinkCells(shrink, zone));
        Assert.Empty(LayoutFieldShrink.ShrinkCells(shrink, []));
    }
    [Fact]
    public void FieldPlacementBuildsAProtectedCellFreeConnectedBlockAndAdoptsExistingFoodInPlace()
    {
        var facts = ColonyFactsCoreTests.Reading().Facts with
        {
            LayoutPlan = new() { Zones = [new() { Kind = LayoutZoneKind.Field, Runs = [new(0, 0, 4)] }] },
            Definitions = [new() { Name = "Wall" }, new() { Name = "Grass", Plant = new() }],
        };
        var cells = Enumerable.Range(0, 4).Select(x => SiteCellFixtures.At(new(x, 0), fertility: 1)).ToArray();
        var census = new RimGovernor.Brain.GameFacts.Observations.ZonesSnapshot
            { Context = facts.Census.Context!, Zones = [] };
        FieldBlockOption option = new(new() { Name = "rice", Edible = true, FertilityMin = .5 }, 2);
        var fresh = LayoutFieldPlanning.PlanFieldBlock(facts, cells, census, new(0, 0), [option], [new(0, 0)]).Edit;
        Assert.NotNull(fresh);
        Assert.Equal("rice", fresh.Crop);
        Assert.Equal("", fresh.Zone);
        Assert.Equal([new Cell(1, 0), new Cell(2, 0)], fresh.Cells);
        cells[0] = SiteCellFixtures.At(new(0, 0), fertility: 1, zoneId: "existing");
        census = census with { Zones = [ZoneFixtures.Growing("existing", "corn", 1, edible: true)] };
        var grow = LayoutFieldPlanning.PlanFieldBlock(facts, cells, census, new(0, 0), [option], []).Edit;
        Assert.NotNull(grow);
        Assert.Equal(("existing", "corn"), (grow.Zone, grow.Crop));
        Assert.Equal([new Cell(1, 0), new Cell(2, 0)], grow.Cells);
    }

    [Fact]
    public void ShrinkUsesHysteresisAndLargestZoneNameTieWithoutAssumingMissingFacts()
    {
        var context = ColonyFactsCoreTests.Snapshot().Context!;
        var census = new RimGovernor.Brain.GameFacts.Observations.ZonesSnapshot
            { Context = context, Zones = [ZoneFixtures.Growing("b", "rice", 3), ZoneFixtures.Growing("a", "rice", 3)] };
        var cells = Enumerable.Range(0, 3).Select(x => SiteCellFixtures.At(new(x, 0), zoneId: "b"))
            .Concat(Enumerable.Range(10, 3).Select(x => SiteCellFixtures.At(new(x, 0), zoneId: "a"))).ToArray();
        var shrink = LayoutFieldShrink.PlanFieldShrink(cells, census, new Dictionary<string, int> { ["rice"] = 4 });
        Assert.NotNull(shrink);
        Assert.Equal(("a", 1), (shrink.Id, shrink.Remove));
        Assert.Null(LayoutFieldShrink.PlanFieldShrink(cells, census, new Dictionary<string, int> { ["rice"] = 5 }));
        Assert.Null(LayoutFieldShrink.PlanFieldShrink(cells, census, new Dictionary<string, int> { ["rice"] = 0 }));
        Assert.Throws<RequiredFactException>(() => LayoutFieldShrink.PlanFieldShrink(cells, null, new Dictionary<string, int>()));
    }
    [Fact]
    public void ShrinkTargetLedgerCombinesHayAndSocialDemandWithoutInventingUnknownHay()
    {
        var targets = LayoutFieldShrink.FieldTargets(new(), ("hay", 12, true), [new() { Name = "social" }]);
        Assert.Equal(12, targets["hay"]);
        Assert.Equal(SocialCropsRules.SocialCropCells, targets["social"]);
        Assert.Empty(LayoutFieldShrink.FieldTargets(new(), ("hay", 12, false), []));
    }
}

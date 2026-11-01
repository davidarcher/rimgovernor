using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class LayoutFieldPlanningTests
{
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
    public void ShrinkSkipsBridgeCellsAndNeverDropsTheLastCell()
    {
        HashSet<Cell> zone = [new(0, 0), new(1, 0), new(2, 0)];
        var shrink = new FieldShrink("zone", "rice", zone, [new(1, 0), new(0, 0), new(2, 0)], 9);
        Assert.Equal([new Cell(0, 0), new Cell(2, 0)], LayoutFieldShrink.ShrinkCells(shrink, zone));
        Assert.Empty(LayoutFieldShrink.ShrinkCells(shrink, []));
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

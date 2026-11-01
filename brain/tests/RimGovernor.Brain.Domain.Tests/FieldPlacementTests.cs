using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Store;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class FieldPlacementTests
{
    private static readonly NeedId Food = new("food/supply");
    private static readonly NeedId Textile = new("economy/resource");

    private static CropChoice Crop(string name, bool edible = true, double sow = 100, double harvest = 200, double grow = 5, double min = 0) => new()
    {
        Name = name, Edible = edible, FertilityMin = .5, FertilitySensitivity = 1, GrowDays = grow,
        SowWork = sow, HarvestWork = harvest, MinGrowthTemperature = min, MaxGrowthTemperature = 58, MinGlow = .5,
    };

    private static FieldPlacementRequest Request(RowRun[] blocks, SiteCell[] cells, FieldDemand[] demands,
        Cell[]? protectedCells = null, ZoneState[]? zones = null, CropChoice[]? crops = null)
    {
        var facts = ColonyFactsCoreTests.Reading().Facts with
        {
            LayoutPlan = new() { Zones = [.. blocks.Select(b => new LayoutZone { Kind = LayoutZoneKind.Field, Runs = [b] })] },
            Definitions = [],
        };
        var snapshot = ColonyFactsCoreTests.Snapshot();
        snapshot = snapshot with { OutdoorTemperatureC = 20, FoodClimate = snapshot.FoodClimate with { GrowingDaysRemaining = 30, SowingNow = true } };
        return new FieldPlacementRequest
        {
            Facts = facts,
            Cells = cells,
            Zones = new ZonesSnapshot { Context = facts.Census.Context!, Zones = [.. zones ?? []] },
            Anchor = new(0, 0),
            Demands = demands,
            Protected = protectedCells ?? [],
            Climate = new CropClimate { Census = snapshot, OutdoorsDark = false },
            Crops = crops ?? [],
        };
    }

    private static SiteCell[] Row(int from, int to, Func<int, SiteCell>? at = null) =>
        [.. Enumerable.Range(from, to - from + 1).Select(x => at is null ? SiteCellFixtures.At(new(x, 0)) : at(x))];

    private static FieldDemand Demand(NeedId need, CropChoice crop, int needed) => new(need, [new FieldBlockOption(crop, needed)]);

    private static FieldPlacementEffect Effect(FieldPlacementAlternative a) => ((FieldPlacementStanding.Assessed)a.Standing).Effect;

    [Fact]
    public void ComparisonRefusesWithoutAPlanOrNeedAndRequiresTheWindowOnceACellIsOwed()
    {
        var r = Request([], [], []);
        Assert.Equal("no layout plan", FieldPlacementRules.Choose(r with { Facts = r.Facts with { LayoutPlan = null } }).Reason);
        Assert.Equal("layout plan has no field blocks", FieldPlacementRules.Choose(r).Reason);
        r = Request([new(0, 0, 4)], [], [Demand(Food, Crop("rice"), 0)]);
        Assert.Equal("no field cells needed", FieldPlacementRules.Choose(r with { Cells = null }).Reason);
        r = Request([new(0, 0, 4)], [], [Demand(Food, Crop("rice"), 4)]);
        Assert.Throws<RequiredFactException>(() => FieldPlacementRules.Choose(r with { Cells = null }));
        Assert.Throws<RequiredFactException>(() => FieldPlacementRules.Choose(r with { Zones = null }));
    }

    [Fact]
    public void TheSecondMoreFertilePatchBeatsTheFirstFeasibleOne()
    {
        // The first block is larger and nearer (FieldBlocks order) but poorer; the old first-feasible return took it.
        var cells = Row(0, 13, x => SiteCellFixtures.At(new(x, 0), fertility: x <= 5 ? .7 : x >= 10 ? 1.4 : 1));
        var choice = FieldPlacementRules.Choose(Request([new(0, 0, 6), new(0, 10, 4)], cells, [Demand(Food, Crop("rice"), 4)]));
        var chosen = Assert.Single(choice.Decisions).Chosen;
        Assert.NotNull(chosen);
        Assert.Equal(1, chosen.Block);
        Assert.Equal([new Cell(10, 0), new Cell(11, 0), new Cell(12, 0), new Cell(13, 0)], chosen.Edit!.Cells);
        var poorer = choice.Decisions[0].Alternatives.Single(a => a.Block == 0);
        Assert.Equal(.7, Effect(poorer).Share, 6);
    }

    [Fact]
    public void FoodAndTextileCompeteForOneFertilePatchWithoutSharingCells()
    {
        var cells = Row(0, 13, x => SiteCellFixtures.At(new(x, 0), fertility: x <= 3 ? 1.4 : x >= 10 ? .8 : 1));
        FieldDemand[] demands = [Demand(Food, Crop("rice"), 4), Demand(Textile, Crop("cotton", edible: false, sow: 300, harvest: 600), 4)];
        var choice = FieldPlacementRules.Choose(Request([new(0, 0, 4), new(0, 10, 4)], cells, demands));
        var rice = choice.Decisions[0].Chosen!;
        var cotton = choice.Decisions[1].Chosen!;
        Assert.Equal((0, 1), (rice.Block, cotton.Block));
        Assert.Empty(rice.Edit!.Cells.Intersect(cotton.Edit!.Cells));
        Assert.Equal(.8, Effect(cotton).Share, 6);

        // The cheaper crop per share served wins the patch whichever Need derived it: food is not privileged.
        demands = [Demand(Food, Crop("rice", sow: 300, harvest: 600), 4), Demand(Textile, Crop("cotton", edible: false), 4)];
        choice = FieldPlacementRules.Choose(Request([new(0, 0, 4), new(0, 10, 4)], cells, demands));
        Assert.Equal((1, 0), (choice.Decisions[0].Chosen!.Block, choice.Decisions[1].Chosen!.Block));

        // With one patch the loser is not credited ground the winner took.
        demands = [Demand(Food, Crop("rice"), 4), Demand(Textile, Crop("cotton", edible: false, sow: 300, harvest: 600), 4)];
        choice = FieldPlacementRules.Choose(Request([new(0, 0, 4)], cells, demands));
        Assert.NotNull(choice.Decisions[0].Chosen);
        Assert.Null(choice.Decisions[1].Chosen);
        Assert.Equal("land:cotton", choice.Decisions[1].Reason);
    }

    [Fact]
    public void AHalfRoofedHalfInfertilePatchIsCreditedOnlyForItsUsableCells()
    {
        var cells = Row(0, 3, x => x == 0 ? SiteCellFixtures.At(new(x, 0), roof: "RoofConstructed")
            : x == 1 ? SiteCellFixtures.At(new(x, 0), fertility: .3) : SiteCellFixtures.At(new(x, 0)));
        var chosen = Assert.Single(FieldPlacementRules.Choose(Request([new(0, 0, 4)], cells, [Demand(Food, Crop("rice"), 4)])).Decisions).Chosen!;
        Assert.Equal([new Cell(2, 0), new Cell(3, 0)], chosen.Edit!.Cells);
        Assert.Equal(.5, Effect(chosen).Share, 6);
    }

    [Fact]
    public void AnOutOfSeasonCropIsExcludedByName()
    {
        var cells = Row(0, 3);
        var cold = FieldPlacementRules.Choose(Request([new(0, 0, 4)], cells, [Demand(Food, Crop("rice", min: 25), 4)])).Decisions[0];
        Assert.Null(cold.Chosen);
        Assert.Equal("growing season:rice", cold.Reason);
        var late = FieldPlacementRules.Choose(Request([new(0, 0, 4)], cells, [Demand(Food, Crop("rice", grow: 40), 4)])).Decisions[0];
        Assert.Null(late.Chosen);
        Assert.Equal("growing season:rice", late.Reason);
    }

    [Fact]
    public void ACoveredBoundZoneIsRetainedAndAnOwedOneGrowsInPlaceDeterministically()
    {
        var cells = Row(0, 3, x => SiteCellFixtures.At(new(x, 0), zoneId: x == 0 ? "existing" : null));
        ZoneState[] zones = [ZoneFixtures.Growing("existing", "corn", 1, edible: true)];
        CropChoice[] crops = [Crop("corn")];
        Assert.Equal("no field cells needed", FieldPlacementRules.Choose(Request([new(0, 0, 4)], cells, [Demand(Food, Crop("rice"), 0)], zones: zones, crops: crops)).Reason);
        var r = Request([new(0, 0, 4)], cells, [Demand(Food, Crop("rice"), 2)], zones: zones, crops: crops);
        var grow = FieldPlacementRules.Choose(r).Decisions[0].Chosen!.Edit!;
        Assert.Equal(("existing", "corn"), (grow.Zone, grow.Crop));
        Assert.Equal([new Cell(1, 0), new Cell(2, 0)], grow.Cells);
        var again = FieldPlacementRules.Choose(r).Decisions[0].Chosen!.Edit!;
        Assert.Equal((grow.Zone, grow.Crop), (again.Zone, again.Crop));
        Assert.Equal(grow.Cells, again.Cells);

        // The zone's crop has no catalog row: its growth is unassessed by name, and a new block still competes.
        var unread = FieldPlacementRules.Choose(r with { Crops = [] }).Decisions[0];
        Assert.Contains(unread.Alternatives, a => a.Standing is FieldPlacementStanding.Unassessed { Fact: "crop definition:corn" });
        Assert.Equal("", unread.Chosen!.Edit!.Zone);
    }

    [Fact]
    public void GroundABuildingSiteHoldsIsNeverTaken()
    {
        var cells = Row(0, 3);
        Cell[] held = [new(0, 0), new(1, 0)];
        var chosen = FieldPlacementRules.Choose(Request([new(0, 0, 4)], cells, [Demand(Food, Crop("rice"), 4)], held)).Decisions[0].Chosen!;
        Assert.Equal([new Cell(2, 0), new Cell(3, 0)], chosen.Edit!.Cells);
        var none = FieldPlacementRules.Choose(Request([new(0, 0, 4)], cells, [Demand(Food, Crop("rice"), 4)], [.. cells.Select(c => c.Cell)])).Decisions[0];
        Assert.Null(none.Chosen);
        Assert.Equal("land:rice", none.Reason);
    }

    private static PlanState CreatePlan(string crop, Cell[] cells, Receipt? receipt)
    {
        IntentActionRules.RegisterIntentKinds();
        var create = DomainAction.NewZoneCreateAction(new("field-0"), new ZoneCreate(RimGovernor.Brain.Domain.ZoneKind.Growing, crop, cells));
        var plan = new PlanSpec(new("field"), new(1), [create]);
        var scope = new GenerationSnapshot(new("colony"), new(1), new("load"), plan.Id, new(1), new(1));
        var progress = new Progress(plan, create.Id).Prepare(scope, new(100)).MarkDispatched(scope, new(100));
        if (receipt is { } r)
        {
            progress = progress.RecordReceipt(progress.View.Attempt, r);
        }
        return new PlanState(false, default, default, plan, [progress], []);
    }

    [Fact]
    public void AnInFlightZoneCreateHoldsItsGroundUntilObservedAndARefusalReleasesIt()
    {
        // Review N bound a create of rice on the only block; review N+1 reads the same facts, the zone not yet observed.
        Cell[] x = [new(0, 0), new(1, 0), new(2, 0), new(3, 0)];
        foreach (var receipt in new Receipt?[] { null, Receipt.Unconfirmed, Receipt.Accepted })
        {
            Assert.Single(FieldPlanner.PendingFieldHolds(Food, [CreatePlan("rice", x, receipt)]));
        }
        var held = FieldPlanner.PendingFieldHolds(Food, [CreatePlan("rice", x, Receipt.Accepted)]).ToArray();
        Assert.Empty(FieldPlanner.PendingFieldHolds(Food, [CreatePlan("rice", x, Receipt.Accepted) with { Retired = true }]));

        FieldDemand[] demands = [Demand(Food, Crop("rice"), 4), Demand(Textile, Crop("cotton", edible: false), 4)];
        var cells = Row(0, 3);
        var r = Request([new(0, 0, 4)], cells, demands) with { Pending = held };
        var pending = FieldPlacementRules.Choose(r);
        Assert.True(pending.Decisions[0].Awaiting);
        Assert.Null(pending.Decisions[0].Chosen);
        Assert.Equal("pending:rice", pending.Decisions[0].Reason);
        Assert.Null(pending.Decisions[1].Chosen);
        Assert.Equal("land:cotton", pending.Decisions[1].Reason);

        // The zone appears: the observed zone replaces the hold, and the ledger grows it as the standing zone.
        var observed = Row(0, 5, c => SiteCellFixtures.At(new(c, 0), zoneId: c <= 3 ? "Zone_1" : null));
        var seen = FieldPlacementRules.Choose(Request([new(0, 0, 6)], observed, [Demand(Food, Crop("rice"), 2)],
            zones: [ZoneFixtures.Growing("Zone_1", "rice", 4, edible: true)], crops: [Crop("rice")]) with { Pending = held }).Decisions[0];
        Assert.False(seen.Awaiting);
        Assert.Equal("Zone_1", seen.Chosen!.Edit!.Zone);

        // A refused create releases its ground: the textile crop may take it.
        var refused = FieldPlanner.PendingFieldHolds(Food, [CreatePlan("rice", x, Receipt.Refused)]).ToArray();
        Assert.Empty(refused);
        var freed = FieldPlacementRules.Choose(Request([new(0, 0, 4)], cells, [demands[1]]) with { Pending = refused });
        Assert.Equal(x, freed.Decisions[0].Chosen!.Edit!.Cells);
    }
}

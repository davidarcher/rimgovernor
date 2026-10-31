using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The first room (#3659): a first plan's rooms are the station graph's. Three calm colonists on a temperate map
/// with trees in reach (logs at chop labor) get bedrooms; with no tree in reach (logs at market) or on an ice sheet,
/// one barracks. A craft order the colony has no
/// bench for gets no host before the shared comparison: each catalog host and deferral is an alternative graph the plan
/// can place (RG-HABITAT-005, WorkshopHostAlternativeTests compares them).
/// </summary>
public sealed class FirstRoomPlanTests
{
    private const string Spot = "CraftingSpot";
    private const string Smithy = "Smithy";
    private const string Knife = "MakeKnife";

    internal static StationFacts Facts(double[] curve, string climate, IReadOnlyList<OrderSpec>? orders = null, bool treeless = false, int colonists = 3)
    {
        var s = FirstLayoutPlanTests.Survey(climate);
        var f = FirstLayoutPlanTests.Facts(s, colonists);
        var calm = BedMergePriceTests.CalmOf(colonists);
        var stations = new Dictionary<string, Station>(f.Catalog.Stations) { ["Bed"] = BedMergePriceTests.Catalog.Stations["Bed"] };
        // The game's crafting spot: no room role, half speed, free to build. The smithy is a workshop bench at full speed.
        stations[Spot] = new Station
        {
            Shape = new InteriorPieceDef(Spot, new Cell(1, 1), RoomRole.None, new InteractionCell.None()),
            Capacity = new StationCapacity { Kind = CapacityKind.Workers, Slots = 1, Recipes = [new RecipeWorkAmount(Knife, 1000)], WorkSpeed = 0.5 },
        };
        stations[Smithy] = new Station
        {
            Shape = new InteriorPieceDef(Smithy, new Cell(3, 1), RoomRole.None, new InteractionCell.None()),
            Contributions = [new RoleScore { Role = RoomRole.Workshop, Weight = 27, Additive = true }],
            Capacity = new StationCapacity { Kind = CapacityKind.Workers, Slots = 1, Recipes = [new RecipeWorkAmount(Knife, 1000)], WorkSpeed = 1 },
        };
        var recipes = new Dictionary<string, RecipeFlow>(f.Recipes) { [Knife] = new RecipeFlow { Products = [new Amount(new Resource("Knife"), 1)], Work = 1000 } };
        return f with
        {
            Catalog = f.Catalog with { Stations = stations, Climate = BedMergePriceTests.Catalog.Climate },
            Recipes = recipes,
            OutdoorCurve = EquatableArray.From(curve),
            BedMerge = treeless ? BedMergePriceTests.MarketWood(calm) : BedMergePriceTests.Wood(calm),
            Crops = [BedMergePriceTests.Rice],
            Ledger = new LedgerView { Status = WorkRules.LedgerViewReconciled, Orders = orders ?? [] },
        };
    }

    internal static LayoutPlan Derive(StationFacts f)
    {
        var (plan, _) = LayoutDeriveRules.DeriveLayoutPlan(f.Survey, f, []);
        Assert.NotNull(plan);
        return plan;
    }

    /// <summary>The first plan of <paramref name="f"/> tiled from the seed of the host alternative <paramref name="id"/>.</summary>
    internal static LayoutPlan DeriveHosted(StationFacts f, string id)
    {
        var prepared = LayoutDeriveRules.FirstPlanFacts(f.Survey, f, []);
        var host = prepared.HostAlternatives().Alternatives.Single(a => a.Id == id);
        var advice = LayoutStationPlanRules.AdviseStations(prepared, LayoutRegroupRules.Seed(LayoutStationPlanRules.RegroupInputOf(prepared, host)));
        var (plan, _) = LayoutDeriveRules.TileFirstPlan(prepared.Survey, prepared, advice);
        Assert.NotNull(plan);
        return plan;
    }

    [Fact]
    public void ATemperateFirstPlanGivesThreeColonistsBedrooms()
    {
        var plan = Derive(Facts(BedMergePriceTests.Temperate, "mild"));

        Assert.Empty(plan.RoomsOf(PlannedRole.Barracks));
        Assert.True(plan.RoomsOf(PlannedRole.Bedroom).Count >= 3, $"bedrooms {plan.RoomsOf(PlannedRole.Bedroom).Count}");
    }

    [Fact]
    public void ATreelessTemperateFirstPlanHousesThreeCalmColonistsInOneBarracks()
    {
        var f = Facts(BedMergePriceTests.Temperate, "mild", treeless: true);
        var (derived, advice) = LayoutDeriveRules.DeriveLayoutPlan(f.Survey, f, []);
        var plan = derived!;

        var barracks = Assert.Single(plan.RoomsOf(PlannedRole.Barracks));
        Assert.Empty(plan.RoomsOf(PlannedRole.Bedroom));
        // The chosen area is one the placer furnishes (#3748): every room with stations tiles with every piece placed,
        // the barracks' three beds and whatever shares its room. (The yard, which holds none, is no room to furnish.)
        var tiled = Assert.IsType<TileResult>(advice.Tiled);
        var furnished = advice.Clustering.Groups.Where(g => g.Stations.Count > 0).ToList();
        Assert.DoesNotContain(tiled.Decisions, d => d.Reason == LayoutTileRules.ReasonTileUnfit && furnished.Any(g => g.Id == d.Group));
        Assert.All(furnished, g => Assert.Equal(g.Stations.Sum(s => s.Count), tiled.Pieces[tiled.Rooms[g.Id]].Count));
        Assert.Equal(3, plan.Pieces[barracks.Id].Count(p => p.Def == "Bed"));
    }

    [Fact]
    public void AnIceSheetFirstPlanHousesThreeCalmColonistsInBedrooms()
    {
        // With one sleep slot a colonist (#3749) a saved room's shell and heat (about 0.3 capability-days on wood) do not
        // outweigh a calm sleeper's 0.24 over the horizon until the colony is larger.
        var plan = Derive(Facts(BedMergePriceTests.IceSheet, "cold"));

        Assert.Empty(plan.RoomsOf(PlannedRole.Barracks));
        Assert.True(plan.RoomsOf(PlannedRole.Bedroom).Count >= 3, $"bedrooms {plan.RoomsOf(PlannedRole.Bedroom).Count}");
    }

    [Fact]
    public void AnIceSheetFirstPlanHousesSixColonistsInOneRoofedBarracks()
    {
        var f = Facts(BedMergePriceTests.IceSheet, "cold", colonists: 6);
        var plan = Derive(f);

        var barracks = Assert.Single(plan.RoomsOf(PlannedRole.Barracks));
        Assert.Empty(plan.RoomsOf(PlannedRole.Bedroom));
        // Roofed: every interior cell is within the roof support distance of the room's walls.
        var b = barracks.Region.Bounds();
        Assert.True(Math.Min(b.Width, b.Height) <= (2 * f.RoofDistance) + 1, $"barracks {b.Width}x{b.Height}");
    }

    private static readonly OrderSpec KnifeOrder = new() { Recipe = Knife, Target = 1, Mode = BillMode.StockTarget };

    /// <summary>The planned piece defs of the plan.</summary>
    private static HashSet<string> PieceDefs(LayoutPlan plan) => [.. plan.Pieces.Values.SelectMany(p => p).Select(p => p.Def)];

    [Fact]
    public void ACraftOrderWithNoBenchOffersEveryCatalogHostAndDeferralAndEachHostGraphPlacesItsBench()
    {
        var f = Facts(BedMergePriceTests.Temperate, "mild", [KnifeOrder]);
        var hosts = f.HostAlternatives();

        // Nothing holds the load yet, so deferral is the baseline and no definition is chosen before the comparison.
        Assert.Equal(["host:baseline", "host:MakeKnife/CraftingSpot", "host:MakeKnife/Smithy"], hosts.Alternatives.Select(a => a.Id));
        Assert.IsType<HostChoice.Deferred>(Assert.Single(hosts.Baseline.Loads).Host);
        Assert.Equal([KnifeOrder], Assert.Single(hosts.Baseline.Loads).Orders);
        Assert.Empty(hosts.Omitted);
        Assert.DoesNotContain(StationGraph.BuildStationGraph(f.GraphInput()).Nodes, n => n.Def == Spot || n.Def == Smithy);
        foreach (var (id, host) in new[] { ("host:MakeKnife/CraftingSpot", Spot), ("host:MakeKnife/Smithy", Smithy) })
        {
            var graph = StationGraph.BuildStationGraph(f.GraphInput(hosts.Alternatives.Single(a => a.Id == id)));
            var node = Assert.Single(graph.Nodes, n => n.Def == Spot || n.Def == Smithy);
            Assert.Equal((host, Knife, 1d), (node.Def, Assert.Single(node.Production).Recipe, node.Production[0].RunsPerDay));
            Assert.Contains(host, PieceDefs(DeriveHosted(f, id)));
        }
    }

    /// <summary>The catalog's knife recipe (RecipeView.CraftRecipes): no standing bench lists it.</summary>
    private static readonly GearRecipe KnifeCraft = new()
    {
        Access = new RecipeAccess.Catalog(Knife, true),
        Products = [new Resource("Knife")],
        Ingredients = EquatableArray.From([EquatableArray.From([new Amount(new Resource("Steel"), 50)])]),
        RequiredWork = [],
        WorkAmount = 3200,
    };

    [Fact]
    public void AWeaponNoBenchMakesIsOrderedFromTheCatalogAndThePlanHoldsItsHost()
    {
        var items = new ItemFacts { Market = new Dictionary<Resource, double> { [new Resource("Steel")] = 1.9 } };
        var method = GearRules.CraftOf([KnifeCraft], [], new Resource("Knife"), 1, items);

        Assert.NotNull(method);
        Assert.Equal((GearMethodKind.Produce, Knife, GearRules.CraftBench), (method.Kind, method.Recipe, method.Bench));
        // No bench stands, so the order names no bench kind and the graph picks the host.
        var order = new OrderSpec { Recipe = method.Recipe, Mode = BillMode.GearBatch, Target = method.Count, BenchKind = GearOrdersRules.GearBenchDef([], method.Bench) };
        Assert.Equal(GearRules.CraftBench, order.BenchKind);
        var plan = DeriveHosted(Facts(BedMergePriceTests.Temperate, "mild", [order]), "host:MakeKnife/CraftingSpot");
        // The lone spot is planned as a workshop (#3659), which the tiler places.
        Assert.Contains(plan.RoomsOf(PlannedRole.Workshop), r => plan.Pieces.TryGetValue(r.Id, out var pieces) && pieces.Any(p => p.Def == Spot));
    }

    [Fact]
    public void TheOrdersBenchKindStillRestrictsTheHost()
    {
        // A standing smithy lists the recipe, so its bills run there alone: no host alternative is offered for the load.
        var f = Facts(BedMergePriceTests.Temperate, "mild", [KnifeOrder with { BenchKind = Smithy }]);
        var node = Assert.Single(StationGraph.BuildStationGraph(f.GraphInput()).Nodes, n => n.Def == Spot || n.Def == Smithy);
        Assert.Equal(Smithy, node.Def);
        Assert.Equal(["host:baseline"], f.HostAlternatives().Alternatives.Select(a => a.Id));
        Assert.Empty(f.HostAlternatives().Baseline.Loads);
    }
}

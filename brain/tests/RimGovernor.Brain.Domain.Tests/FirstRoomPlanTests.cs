using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The first room (#3659): a first plan's rooms are the station graph's. Three calm colonists on a temperate map
/// with trees in reach (logs at chop labor) get bedrooms; with no tree in reach (logs at market) or on an ice sheet,
/// one barracks. A craft order the colony has no
/// bench for still gets a host node, the one its price chooses, and the plan places it.
/// </summary>
public sealed class FirstRoomPlanTests
{
    private const string Spot = "CraftingSpot";
    private const string Smithy = "Smithy";
    private const string Knife = "MakeKnife";

    internal static StationFacts Facts(double[] curve, string climate, IReadOnlyList<OrderSpec>? orders = null, IReadOnlyDictionary<string, double>? hostBuild = null, bool treeless = false, int colonists = 3)
    {
        var s = FirstLayoutPlanTests.Survey(climate);
        var f = FirstLayoutPlanTests.Facts(s, colonists);
        var calm = BedMergePriceTests.CalmOf(colonists);
        var stations = new Dictionary<string, Station>(f.Catalog.Stations) { ["Bed"] = BedMergePriceTests.Catalog.Stations["Bed"] };
        // The game's crafting spot: no room role, half speed, free to build. The smithy is a workshop bench at full speed.
        stations[Spot] = new Station
        {
            Shape = new InteriorPieceDef(Spot, new Cell(1, 1), RoomRole.None, null),
            Capacity = new StationCapacity { Kind = CapacityKind.Workers, Slots = 1, Recipes = [new RecipeWorkAmount(Knife, 1000)], WorkSpeed = 0.5 },
        };
        stations[Smithy] = new Station
        {
            Shape = new InteriorPieceDef(Smithy, new Cell(3, 1), RoomRole.None, null),
            Contributions = [new RoleScore { Role = RoomRole.Workshop, Weight = 27, Additive = true }],
            Capacity = new StationCapacity { Kind = CapacityKind.Workers, Slots = 1, Recipes = [new RecipeWorkAmount(Knife, 1000)], WorkSpeed = 1 },
        };
        var recipes = new Dictionary<string, RecipeFlow>(f.Recipes) { [Knife] = new RecipeFlow { Products = [new Amount(new Resource("Knife"), 1)] } };
        return f with
        {
            Catalog = f.Catalog with { Stations = stations, Climate = BedMergePriceTests.Catalog.Climate },
            Recipes = recipes,
            OutdoorCurve = Known(EquatableArray.From(curve)),
            BedMerge = treeless ? BedMergePriceTests.MarketWood(calm) : BedMergePriceTests.Wood(calm),
            Crops = [BedMergePriceTests.Rice],
            Orders = Known(EquatableArray.From(orders ?? [])),
            HostBuild = hostBuild ?? new Dictionary<string, double>(),
        };
    }

    internal static LayoutPlan Derive(StationFacts f)
    {
        var (plan, _) = LayoutDeriveRules.DeriveLayoutPlan(f.Survey, f, []);
        Assert.True(plan.IsKnown, "no first plan");
        return plan.Value;
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
        var plan = derived.Value;

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

    [Theory]
    // Over the 60-day horizon one knife a day costs 2 work-days at the spot's half speed and 1 at the smithy: a smithy
    // dearer than one work-day to build loses to the free spot, a cheaper one wins.
    [InlineData(3.0, Spot)]
    [InlineData(0.5, Smithy)]
    public void ACraftOrderWithNoBenchGetsThePriceChosenHostNodeAndThePlanPlacesIt(double smithyBuild, string host)
    {
        var f = Facts(BedMergePriceTests.Temperate, "mild", [KnifeOrder], new Dictionary<string, double> { [Spot] = 0, [Smithy] = smithyBuild });
        var graph = StationGraph.BuildStationGraph(f.GraphInput());

        var work = graph.Nodes.Where(n => n.Purpose == StationPurpose.Work && (n.Def == Spot || n.Def == Smithy)).ToList();
        var node = Assert.Single(work);
        Assert.Equal(host, node.Def);
        Assert.Contains(host, PieceDefs(Derive(f)));
    }

    /// <summary>The catalog's knife recipe (RecipeView.CraftRecipes): no standing bench lists it.</summary>
    private static readonly GearRecipe KnifeCraft = new()
    {
        Definition = Knife,
        Products = [new Resource("Knife")],
        Available = Known(true),
        AvailableOn = Known(true),
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
        var plan = Derive(Facts(BedMergePriceTests.Temperate, "mild", [order], new Dictionary<string, double> { [Spot] = 0, [Smithy] = 3 }));
        // The lone spot is planned as a workshop (#3659), which the tiler places.
        Assert.Contains(plan.RoomsOf(PlannedRole.Workshop), r => plan.Pieces.TryGetValue(r.Id, out var pieces) && pieces.Any(p => p.Def == Spot));
    }

    [Fact]
    public void AnUnreadHostBuildKeepsTheFirstHostByName()
    {
        var f = Facts(BedMergePriceTests.Temperate, "mild", [KnifeOrder], new Dictionary<string, double> { [Spot] = 0 });
        var node = Assert.Single(StationGraph.BuildStationGraph(f.GraphInput()).Nodes, n => n.Def == Spot || n.Def == Smithy);
        Assert.Equal(Spot, node.Def);
    }

    [Fact]
    public void TheOrdersBenchKindStillRestrictsTheHost()
    {
        var f = Facts(BedMergePriceTests.Temperate, "mild", [KnifeOrder with { BenchKind = Smithy }], new Dictionary<string, double> { [Spot] = 0, [Smithy] = 30 });
        var node = Assert.Single(StationGraph.BuildStationGraph(f.GraphInput()).Nodes, n => n.Def == Spot || n.Def == Smithy);
        Assert.Equal(Smithy, node.Def);
    }
}

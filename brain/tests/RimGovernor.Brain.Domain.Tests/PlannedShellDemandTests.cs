using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class PlannedShellDemandTests
{
    private static readonly Resource Wood = new("WoodLog");

    private static Rectangle Rect(int x, int z) => new() { X = x, Z = z, Width = 3, Height = 3 };

    private static LayoutPlan Plan() => new()
    {
        Rooms =
        [
            new PlannedRoom { Id = 1, Role = PlannedShelter, Region = RectRegion(Rect(0, 0)) },
            new PlannedRoom { Id = 2, Role = PlannedKitchen, Region = RectRegion(Rect(4, 0)) },
        ],
    };

    private static IReadOnlyList<Amount>? Price(string def) => [new Amount(Wood, 5)];

    [Fact]
    public void WholePlanIsDemandedAndASharedWallIsCountedOnce()
    {
        // Two 3x3 rooms with a wall between: 16 + 16 - 5 shared ring cells.
        var demand = PlannedShellDemand(Plan(), new GroundCensus(), Price);
        Assert.Equal(27 * 5, demand[Wood]);
    }

    [Fact]
    public void StandingWallsAreNoLongerOwed()
    {
        var ground = new GroundCensus();
        foreach (var c in Plan().Rooms[0].Region.Walls().Cells())
        {
            ground.Walls.Add(c);
        }
        var demand = PlannedShellDemand(Plan(), ground, Price);
        Assert.Equal((27 - 16) * 5, demand[Wood]);
    }

    [Fact]
    public void AnUnavailableDefAddsNothing() =>
        Assert.Empty(PlannedShellDemand(Plan(), new GroundCensus(), _ => null));

    [Fact]
    public void ConstructionDemandRaisesTheWholePlanWhereStockFallsShort()
    {
        var planned = new Dictionary<Resource, long> { [Wood] = 135 };
        var stock = new StockReader(Fact.Known(EquatableArray.From(new[] { new Amount(Wood, 40) })), Fact.Unknown<long>());
        var demand = ConstructionDemand(new ConstructionDemandInput { Stock = stock, Planned = Fact.Known<IReadOnlyDictionary<Resource, long>>(planned) });
        Assert.Equal(135, demand[Wood]);
    }

    private static FlooringFacts Flooring(Fact<FlooringObservation> observed = default) => new()
    {
        Definitions = new Dictionary<string, FloorDefinition>
        {
            ["WoodPlankFloor"] = new()
            {
                Available = Fact.Known(true),
                Terrain = Fact.Known(true),
                Cleanliness = Fact.Known(0.0),
                Beauty = Fact.Known(0.0),
                Flammability = Fact.Known(1.0),
                PathCost = Fact.Known(0),
                Costs = Fact.Known(EquatableArray.From(new[] { new Amount(Wood, 2) })),
                WorkToBuild = Fact.Known(100.0),
            },
        },
        Style = _ => ("WoodPlankFloor", true),
        Observation = observed,
        Stock = Fact.Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long>()),
    };

    [Fact]
    public void FloorsAreDemandedBeforeTheColonyCanPayForThem()
    {
        // The stock is empty, yet the bedroom's nine floor cells are demanded in full.
        var plan = new LayoutPlan { Rooms = [new PlannedRoom { Id = 1, Role = PlannedBedroom, Region = RectRegion(Rect(0, 0)) }] };
        var demand = PlannedFloorDemand(plan, Flooring(), DefaultFlooringPolicy());
        Assert.Equal(9 * 2, demand[Wood]);
    }

    [Fact]
    public void StoredStationsAreDemandedUntilTheyStand()
    {
        var bench = new InteriorPiece { Slot = "bench", Def = "TableButcher", Size = new Cell(1, 1), Rect = new Rectangle { X = 1, Z = 1, Width = 1, Height = 1 } };
        var plan = new LayoutPlan
        {
            Rooms = [new PlannedRoom { Id = 1, Role = PlannedKitchen, Region = RectRegion(Rect(0, 0)) }],
            Pieces = new Dictionary<int, IReadOnlyList<InteriorPiece>> { [1] = [bench, bench with { Slot = "bench2", Rect = new Rectangle { X = 2, Z = 1, Width = 1, Height = 1 } }] },
        };
        IReadOnlyList<Amount>? price(string def) => [new Amount(Wood, 20)];
        Assert.Equal(40, PlannedPieceDemand(plan, new HashSet<(string, Cell)>(), price)[Wood]);
        Assert.Equal(20, PlannedPieceDemand(plan, new HashSet<(string, Cell)> { ("TableButcher", bench.Anchor()) }, price)[Wood]);
    }
}

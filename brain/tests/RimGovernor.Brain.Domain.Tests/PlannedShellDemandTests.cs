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

    private static readonly Func<string, IReadOnlyList<Amount>?> NoPrice = _ => null;

    /// <summary>Prices what Reconcile says the plan owes, the way the runtime does: the ring, pieces and floors each by their own list.</summary>
    private static Dictionary<Resource, long> Owed(LayoutPlan plan, GroundCensus ground, Func<string, IReadOnlyList<Amount>?> shell,
        Func<string, IReadOnlyList<Amount>?>? piece = null, Func<string, IReadOnlyList<Amount>?>? floor = null,
        Func<Cell, string>? wantedFloor = null, IReadOnlyList<ClearanceTarget>? rows = null,
        IReadOnlySet<(string, Cell)>? placed = null)
    {
        var owed = plan.Rooms.Select(room => (room, Reconcile(new ReconcileInput
        {
            Plan = plan,
            Room = room,
            Ground = ground,
            Rows = rows ?? [],
            Furniture = plan.Pieces.TryGetValue(room.Id, out var pieces) ? [.. pieces.Select(p => p.Wanted())] : [],
            WantedFloor = wantedFloor,
        }).Owed)).ToList();
        return PlannedTotal(PlannedOwedCost(owed, shell, piece ?? NoPrice, floor ?? NoPrice, "Vent", placed ?? new HashSet<(string, Cell)>()).Values);
    }

    private static IReadOnlyList<Amount>? Price(string def) => [new Amount(Wood, 5)];

    [Fact]
    public void WholePlanIsDemandedAndASharedWallIsCountedOnce()
    {
        // Two 3x3 rooms with a wall between: 16 + 16 - 5 shared ring cells.
        var demand = Owed(Plan(), new GroundCensus(), Price);
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
        var demand = Owed(Plan(), ground, Price);
        Assert.Equal((27 - 16) * 5, demand[Wood]);
    }

    [Fact]
    public void AWallAlreadyPlacedAsABlueprintIsOwedByTheCensusNotThePlan()
    {
        var (wallDef, _) = Plan().Rooms[0].RingDefs();
        var cells = Plan().Rooms[0].Region.Walls().Cells().Take(4).ToList();
        var placed = new HashSet<(string, Cell)>(cells.Select(c => (wallDef, c)));
        var demand = Owed(Plan(), new GroundCensus(), Price, placed: placed);
        Assert.Equal((27 - 4) * 5, demand[Wood]);
    }

    [Fact]
    public void AnUnavailableDefAddsNothing() =>
        Assert.Empty(Owed(Plan(), new GroundCensus(), _ => null));

    [Fact]
    public void ConstructionDemandRaisesTheWholePlanWhereStockFallsShort()
    {
        var planned = new Dictionary<Resource, long> { [Wood] = 135 };
        var stock = new StockReader(Fact.Known(EquatableArray.From(new[] { new Amount(Wood, 40) })), Fact.Unknown<long>());
        var demand = ConstructionDemand(new ConstructionDemandInput { Stock = stock, Planned = Fact.Known<IReadOnlyDictionary<Resource, long>>(planned) });
        Assert.Equal(135, demand[Wood]);
    }

    [Fact]
    public void FloorsAreDemandedBeforeTheColonyCanPayForThem()
    {
        // Nothing is paid for yet, and the bedroom's nine floor cells are demanded in full.
        var plan = new LayoutPlan { Rooms = [new PlannedRoom { Id = 1, Role = PlannedBedroom, Region = RectRegion(Rect(0, 0)) }] };
        var demand = Owed(plan, new GroundCensus(), NoPrice, null, name => [new Amount(Wood, 2)], _ => "WoodPlankFloor");
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
        Assert.Equal(40, Owed(plan, new GroundCensus(), NoPrice, price)[Wood]);
        Assert.Equal(20, Owed(plan, new GroundCensus(), NoPrice, price, rows: [new ClearanceTarget { DefName = "TableButcher", Minimum = bench.Anchor(), Maximum = bench.Anchor(), Player = true }])[Wood]);
    }
}

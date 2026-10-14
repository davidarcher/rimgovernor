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
}

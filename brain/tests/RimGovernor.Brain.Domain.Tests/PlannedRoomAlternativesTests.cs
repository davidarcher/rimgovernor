using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class PlannedRoomAlternativesTests
{
    private static readonly Resource Wood = new("WoodLog");
    private static readonly Resource Steel = new("Steel");

    private static readonly string[] Stuffs = ["Steel", "WoodLog"];

    private static IReadOnlyList<(PlannedRoom, IReadOnlyList<Operation>)> Owed(LayoutPlan plan) =>
        [.. plan.Rooms.Select(room => (room, Reconcile(new ReconcileInput
        {
            Plan = plan, Room = room, Ground = new GroundCensus(), Rows = [],
            Furniture = plan.Pieces.TryGetValue(room.Id, out var pieces) ? [.. pieces.Select(p => p.Wanted())] : [],
            WantedFloor = _ => "WoodPlankFloor",
        }).Owed))];

    private static LayoutPlan Plan() => new()
    {
        Rooms = [new PlannedRoom { Id = 1, Role = PlannedKitchen, Region = RectRegion(new Rectangle { X = 0, Z = 0, Width = 3, Height = 3 }) }],
        Pieces = new Dictionary<int, IReadOnlyList<InteriorPiece>>
        {
            [1] = [new InteriorPiece { Slot = "bench", Def = "TableButcher", Size = new Cell(1, 1), Rect = new Rectangle { X = 1, Z = 1, Width = 1, Height = 1 } }],
        },
    };

    private static Func<string, IReadOnlyList<Amount>?> Shell(string stuff) =>
        _ => [new Amount(stuff == "Steel" ? Steel : Wood, stuff == "Steel" ? 2 : 5)];

    private static IReadOnlyList<PlannedRoomAlternative> Alternatives() =>
        PlannedRoomAlternatives(Owed(Plan()), Stuffs, Shell, _ => [new Amount(Wood, 20)], _ => [new Amount(Wood, 1)], "Vent",
            new HashSet<(string, Cell)>());

    private static long Claim(PlannedRoomAlternative o, Resource r) =>
        (long)o.Claims.MaterialClaims.Single(c => c.Resource == r).Units.Value;

    [Fact]
    public void EachStuffThatMakesTheShellIsOneExclusiveOptionPricedFromItsOwnCosts()
    {
        var options = Alternatives();
        var steel = options.Single(o => o.Id == "planned/room1/Steel");
        var wood = options.Single(o => o.Id == "planned/room1/WoodLog");
        // 16 ring cells, 9 floor cells at 1 wood, one bench at 20 wood.
        Assert.Equal(16 * 2, Claim(steel, Steel));
        Assert.Equal(9 + 20, Claim(steel, Wood));
        Assert.Equal(16 * 5 + 9 + 20, Claim(wood, Wood));
        Assert.DoesNotContain(wood.Claims.MaterialClaims, c => c.Resource == Steel);
        Assert.Contains("planned_construction_labor", wood.Claims.UnsupportedEffects);
    }

    [Fact]
    public void ScopeOptionsFilterOperationKindsOfTheSameOwedDiff()
    {
        var options = Alternatives();
        Assert.Equal(16 * 5, Claim(options.Single(o => o.Id == "planned/room1/WoodLog/Shell"), Wood));
        Assert.Equal(16 * 5 + 9, Claim(options.Single(o => o.Id == "planned/room1/WoodLog/ShellFloor"), Wood));
        Assert.DoesNotContain(options.Single(o => o.Id == "planned/room1/Steel/Shell").Claims.MaterialClaims, c => c.Resource == Wood);
    }

    [Fact]
    public void AStufflessRingHasOnlyScopeAlternativesAndNothingOwedHasNone()
    {
        var plain = PlannedRoomAlternatives(Owed(Plan()), [], Shell, _ => null, _ => null, "Vent", new HashSet<(string, Cell)>());
        Assert.Equal(["planned/room1/Shell", "planned/room1/ShellFloor", "planned/room1"], plain.Select(o => o.Id));
        Assert.Empty(PlannedRoomAlternatives(Owed(Plan()), Stuffs, _ => _ => null, _ => null, _ => null, "Vent", new HashSet<(string, Cell)>()));
    }
}

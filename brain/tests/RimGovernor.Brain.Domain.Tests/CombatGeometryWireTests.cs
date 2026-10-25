using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3780: the combat geometry read is a plain required request and reply; the propose role is a union whose rescue
// route names its own rescuer, and a refused read throws instead of answering a Failure case.
public sealed class CombatGeometryWireTests
{
    private static readonly RimGovernor.Brain.GameFacts.Common.Identity Identity = DefenseWire.Context.Identity;

    private static RimGovernor.Brain.GameFacts.Mirror.CombatGeometryCell Scored(int x, int z, bool lineOfFire) => new()
    {
        Cell = new RimGovernor.Brain.Domain.Cell { X = x, Z = z },
        Lines = [new RimGovernor.Brain.GameFacts.Mirror.CombatSightLine { HostileId = new PawnId("Thing_Raider1"), Cover = 0.5, LineOfFire = lineOfFire, ColonistInPath = false, HostileCover = 0.25 }],
        Standable = true,
        HostileLineOfFire = lineOfFire,
        Door = false,
    };

    private static void RoundTrips<T>(T payload)
    {
        var json = OrdersJson.Serialize(payload);
        Assert.Equal(json, OrdersJson.Serialize(OrdersJson.Deserialize<T>(json)));
        Assert.Equal(payload, OrdersJson.Deserialize<T>(json));
    }

    public static TheoryData<string> Roles => ["cover", "choke", "firing", "rescue", "none"];

    private static RimGovernor.Brain.GameFacts.Mirror.CombatGeometryPropose? Role(string role) => role switch
    {
        "cover" => new RimGovernor.Brain.GameFacts.Mirror.CombatGeometryPropose.CoverBehindLine { Line = [new RimGovernor.Brain.Domain.Cell { X = 1, Z = 2 }] },
        "choke" => new RimGovernor.Brain.GameFacts.Mirror.CombatGeometryPropose.AdjacentToChoke { Choke = new RimGovernor.Brain.Domain.Cell { X = 4, Z = 4 }, OurSide = new RimGovernor.Brain.Domain.Cell { X = 4, Z = 1 } },
        "firing" => new RimGovernor.Brain.GameFacts.Mirror.CombatGeometryPropose.FiringCells { Targets = [new RimGovernor.Brain.Domain.Cell { X = 9, Z = 9 }], From = new RimGovernor.Brain.Domain.Cell { X = 3, Z = 3 }, Radius = 12 },
        "rescue" => new RimGovernor.Brain.GameFacts.Mirror.CombatGeometryPropose.RescuePath { Pawn = new PawnId("Thing_Human1"), To = new RimGovernor.Brain.Domain.Cell { X = 7, Z = 7 } },
        _ => null,
    };

    [Theory]
    [MemberData(nameof(Roles))]
    public void TheRequestRoundTripsExactly(string role) =>
        RoundTrips(new RimGovernor.Brain.GameOrders.Mirror.CombatGeometryRequest { Identity = Identity, Cells = [], HostileIds = [new PawnId("Thing_Raider1")], Propose = Role(role) });

    [Fact]
    public void TheGeometryRoundTripsExactly() =>
        RoundTrips(new RimGovernor.Brain.GameFacts.Mirror.CombatGeometry { Context = DefenseWire.Context, Cells = [Scored(1, 1, true)], Proposed = [Scored(2, 1, false)], MainThreadMs = 1.5 });
}

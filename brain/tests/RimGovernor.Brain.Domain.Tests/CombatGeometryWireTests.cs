using RimGovernor.Contracts.Wire;
using Xunit;
using C = RimGovernor.Contracts.Wire.Common;
using M = RimGovernor.Contracts.Wire.Mirror;

namespace RimGovernor.Brain.Domain.Tests;

// #3780: the combat geometry read is a plain required request and reply; the propose role is a union whose rescue
// route names its own rescuer, and a refused read throws instead of answering a Failure case.
public sealed class CombatGeometryWireTests
{
    private static readonly C.Identity Identity = DefenseWire.Context.Identity;

    private static M.CombatGeometryCell Scored(int x, int z, bool lineOfFire) => new()
    {
        Cell = new C.Cell { X = x, Z = z },
        Lines = [new M.CombatSightLine { HostileId = new PawnId("Thing_Raider1"), Cover = 0.5, LineOfFire = lineOfFire, ColonistInPath = false, HostileCover = 0.25 }],
        Standable = true,
        HostileLineOfFire = lineOfFire,
        Door = false,
    };

    private static void RoundTrips<T>(T payload)
    {
        var json = WireJson.Serialize(payload);
        Assert.Equal(json, WireJson.Serialize(WireJson.Deserialize<T>(json)));
        Assert.Equal(payload, WireJson.Deserialize<T>(json));
    }

    public static TheoryData<string> Roles => ["cover", "choke", "firing", "rescue", "none"];

    private static M.CombatGeometryPropose? Role(string role) => role switch
    {
        "cover" => new M.CombatGeometryPropose.CoverBehindLine { Line = [new C.Cell { X = 1, Z = 2 }] },
        "choke" => new M.CombatGeometryPropose.AdjacentToChoke { Choke = new C.Cell { X = 4, Z = 4 }, OurSide = new C.Cell { X = 4, Z = 1 } },
        "firing" => new M.CombatGeometryPropose.FiringCells { Targets = [new C.Cell { X = 9, Z = 9 }], From = new C.Cell { X = 3, Z = 3 }, Radius = 12 },
        "rescue" => new M.CombatGeometryPropose.RescuePath { Pawn = new PawnId("Thing_Human1"), To = new C.Cell { X = 7, Z = 7 } },
        _ => null,
    };

    [Theory]
    [MemberData(nameof(Roles))]
    public void TheRequestRoundTripsExactly(string role) =>
        RoundTrips(new M.CombatGeometryRequest { Identity = Identity, Cells = [], HostileIds = [new PawnId("Thing_Raider1")], Propose = Role(role) });

    [Fact]
    public void TheGeometryRoundTripsExactly() =>
        RoundTrips(new M.CombatGeometry { Context = DefenseWire.Context, Cells = [Scored(1, 1, true)], Proposed = [Scored(2, 1, false)], MainThreadMs = 1.5 });
}

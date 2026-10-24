using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Contracts.Wire;
using Xunit;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

// #3780: the defense site census, the lines of fire and the spatial access audit are plain required records whose
// unknowns are named cases (a fogged cell or line, a stranded or skipped pawn); each round-trips exactly through the
// wire JSON.
public sealed class DefenseFamilyWireTests
{
    private static readonly O.DefenseSiteSnapshot Site = new()
    {
        Context = DefenseWire.Context,
        Cells =
        [
            new O.DefenseCell.Fogged { Cell = new Cell(0, 0) },
            DefenseWire.Open(new Cell(1, 0)),
            DefenseWire.Open(new Cell(2, 0)) with
            {
                Walkable = false,
                Passable = false,
                CoverFill = 1,
                Roofed = true,
                Edifice = new O.DefenseEdifice { DefName = "Wall", Stuff = "WoodLog", PlayerOwned = true, NaturalRock = false, Door = false },
                Cover = new O.DefenseCover { ThingId = "Thing_Wall4", DefName = "Wall", Kind = O.CoverKind.Building, Designated = false },
            },
            DefenseWire.Open(new Cell(3, 0)) with
            {
                CoverFill = 0.25,
                Cover = new O.DefenseCover { ThingId = "Thing_Plant_Grass9", DefName = "Plant_Grass", Kind = O.CoverKind.Plant, Designated = true },
            },
        ],
    };

    private static readonly O.LinesOfFireSnapshot Lines = DefenseWire.NoLines with
    {
        Lines =
        [
            new O.LineOfFire.Fogged { From = new Cell(1, 1), To = new Cell(9, 9), Distance = 11.3 },
            new O.LineOfFire.Traced { From = new Cell(1, 1), To = new Cell(4, 1), Distance = 3, LineOfSight = true },
        ],
        Skipped = true,
    };

    private static readonly O.SpatialAccessSnapshot Access = DefenseWire.Access(
        new O.PawnAccess.Audited { Pawn = new PawnId("Thing_Human1"), Position = new Cell(5, 5), Targets = [DefenseWire.Target(new Cell(8, 8), true, true)] },
        new O.PawnAccess.Stranded { Pawn = new PawnId("Thing_Human2"), Position = new Cell(6, 5) },
        new O.PawnAccess.Skipped { Pawn = new PawnId("Thing_Human3"), Position = new Cell(7, 5) });

    private static void RoundTrips<T>(T payload)
    {
        var json = WireJson.Serialize(payload);
        Assert.Equal(json, WireJson.Serialize(WireJson.Deserialize<T>(json)));
    }

    [Fact]
    public void TheDefenseSiteRoundTripsExactly() => RoundTrips(Site);

    [Fact]
    public void TheLinesOfFireRoundTripExactly() => RoundTrips(Lines);

    [Fact]
    public void NoLinesRoundTripExactly() => RoundTrips(DefenseWire.NoLines);

    [Fact]
    public void TheAccessAuditRoundTripsExactly() => RoundTrips(Access);

    [Fact]
    public void ACoverKindNamesItsDesignation()
    {
        Assert.Equal(CoverClearanceDesignation.Deconstruct, ((O.DefenseCell.Seen)Site.Cells[2]).Cover!.Designation());
        Assert.Equal(CoverClearanceDesignation.CutPlant, ((O.DefenseCell.Seen)Site.Cells[3]).Cover!.Designation());
    }

    [Fact]
    public void AStrandedPawnRefusesTheLayout() =>
        Assert.Equal("pawn Thing_Human2 at {6 5} has no safe exit", Access.Refusal());

    [Fact]
    public void ASkippedPawnIsNamedAndNotJudged()
    {
        var skipped = DefenseWire.Access(new O.PawnAccess.Skipped { Pawn = new PawnId("Thing_Human3"), Position = new Cell(7, 5) });
        Assert.Equal(("Thing_Human3", true), skipped.Skipped());
        Assert.Equal("", skipped.Refusal());
    }

    [Fact]
    public void ALostTargetRefusesTheLayout()
    {
        var lost = DefenseWire.Access(new O.PawnAccess.Audited { Pawn = new PawnId("Thing_Human1"), Position = new Cell(5, 5), Targets = [DefenseWire.Target(new Cell(8, 8), true, false)] });
        Assert.Equal("pawn Thing_Human1 at {5 5} loses target {8 8}", lost.Refusal());
        Assert.Equal(("", false), lost.Skipped());
    }
}

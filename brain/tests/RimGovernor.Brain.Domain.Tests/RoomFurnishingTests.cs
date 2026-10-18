using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class RoomFurnishingTests
{
    private const string Bed = "Bed";
    private const string Lever = "SculptureSmall";

    private static readonly InteriorPieceDef BedShape = new(Bed, new Cell(1, 2), RoomRole.Bedroom, null);

    private static PieceShapes Shapes
    {
        get
        {
            var lamp = InteriorRegionRules.InteriorDefs().LampDef;
            return new()
            {
                Defs = new Dictionary<string, InteriorPieceDef>
                {
                    [Bed] = BedShape,
                    ["EndTable"] = new("EndTable", new Cell(1, 1), RoomRole.None, null),
                    ["Dresser"] = new("Dresser", new Cell(1, 1), RoomRole.None, null),
                    [lamp] = new(lamp, new Cell(1, 1), RoomRole.None, null),
                },
                Furniture = new RoomFurniture
                {
                    Beds = [new FurnitureBed { Def = Bed, Slots = 1 }],
                    EndTable = new FacilityLink { Def = "EndTable" },
                    Dresser = new FacilityLink { Def = "Dresser" },
                    AnimalSpot = "AnimalSpotBed",
                },
            };
        }
    }

    private static PlannedRoom Room() => new()
    {
        Id = 1,
        Role = PlannedRole.Bedroom,
        Region = RegionRules.RectRegion(new Rectangle { X = 10, Z = 10, Width = 5, Height = 5 }),
        Door = new Cell(12, 9),
        DoorRot = Rotation.South,
    };

    private static InteriorRoom Interior(PlannedRoom room)
    {
        var (interior, ok) = InteriorRules.InteriorRoomFromLayout(room, Shapes);
        Assert.True(ok);
        return interior!;
    }

    private static FurnishingWant BedWant => new() { Key = "bed", Things = [Bed], Count = 1, Piece = BedShape };

    private static CurrentBuilding Standing(string def, int x, int z, int width = 1, int height = 1) => new()
    {
        Id = $"{def}{x}_{z}",
        Building = new Building(def, new Cell(x, z), Rotation.North, ""),
        Cells = [.. Enumerable.Range(0, width).SelectMany(dx => Enumerable.Range(0, height).Select(dz => new Cell(x + dx, z + dz)))],
    };

    private static CurrentBuilding StandingAt(WantedPiece p) =>
        Standing(p.DefName, p.Minimum.X, p.Minimum.Z, p.Maximum.X - p.Minimum.X + 1, p.Maximum.Z - p.Minimum.Z + 1);

    [Fact]
    public void AFreshBedroomListsTheBedroomTemplateBed()
    {
        var room = Room();
        var furnishing = SleepingRules.BedroomFurnishing(room, Shapes, Bed, [], null);
        var bed = Assert.Single(furnishing.Pieces);
        Assert.Equal("bed", bed.Slot);
        Assert.Equal(Bed, bed.DefName);
        Assert.Equal(RoomFurnishingRules.RoomFurnishing(room, Interior(room), [BedWant], []).Pieces, furnishing.Pieces);
        Assert.True(furnishing.Absent);
    }

    [Fact]
    public void ABedroomWithALampDresserAndCompanionBedStandingIsReconciledWithNoPackAcrossRounds()
    {
        var room = Room();
        var plan = new LayoutPlan { Rooms = [room] };
        var bed = StandingAt(SleepingRules.BedroomFurnishing(room, Shapes, Bed, [], null).Pieces.Single());
        var taken = bed.Cells.ToHashSet();
        var free = room.Region.Cells().Where(c => !taken.Contains(c)).ToList();
        var lamp = Standing(InteriorHelpersRules.StandingLampDef, free[0].X, free[0].Z);
        var dresser = Standing("Dresser", free[1].X, free[1].Z);
        var spot = Standing("AnimalSpotBed", free[2].X, free[2].Z);
        var endTable = new RoomUpgrade { Room = "1", Slot = "end_table", Def = "EndTable", Size = new Cell(1, 1), Anchor = free[3], Rot = Rotation.North };

        List<Operation> Owed(Furnishing furnishing, params CurrentBuilding[] standing)
        {
            var rows = standing.Select(b => new ClearanceTarget
            {
                EntityId = b.Id,
                DefName = b.Building!.Definition,
                Minimum = b.Cells[0],
                Maximum = b.Cells[^1],
                Player = true,
                Packable = true,
            }).ToList();
            var input = new ReconcileInput { Plan = plan, Room = room, Ground = new GroundCensus(), Rows = ReconcileRules.OwnRows(rows, furnishing.Pieces, null), Furniture = furnishing.Pieces };
            return [.. ReconcileRules.Reconcile(input).Owed];
        }

        void NoRemoval(IEnumerable<Operation> ops) =>
            Assert.DoesNotContain(ops, o => o.Kind is OpKind.Pack or OpKind.PackInUse or OpKind.FurnitureOut);

        // Round 1: the lever pieces stand and no lever asks for more: every piece is listed where it stands, nothing is owed.
        var round1 = SleepingRules.BedroomFurnishing(room, Shapes, "", [bed, lamp, dresser, spot], null);
        Assert.Equal(3, round1.Pieces.Count);
        Assert.False(round1.Absent);
        var ops1 = Owed(round1, bed, lamp, dresser, spot);
        NoRemoval(ops1);
        Assert.DoesNotContain(ops1, o => o.Kind is OpKind.Build or OpKind.Install);

        // Round 2: a lever asks for an end table: the standing pieces stay listed and only the end table is built.
        var round2 = SleepingRules.BedroomFurnishing(room, Shapes, "", [bed, lamp, dresser, spot], endTable);
        Assert.True(round2.Absent);
        Assert.Equal(4, round2.Pieces.Count);
        Assert.Contains(round2.Pieces, p => p.DefName == "EndTable" && p.Minimum == free[3]);
        var ops2 = Owed(round2, bed, lamp, dresser, spot);
        NoRemoval(ops2);
        Assert.Equal("EndTable", ops2.Single(o => o.Kind == OpKind.Build).Pieces.Single().DefName);

        // Round 3: the end table stands and the lever is satisfied: the list is the same pieces, nothing is owed.
        var endStanding = Standing("EndTable", free[3].X, free[3].Z);
        var round3 = SleepingRules.BedroomFurnishing(room, Shapes, "", [bed, lamp, dresser, spot, endStanding], null);
        Assert.False(round3.Absent);
        Assert.Equal(round2.Pieces.Select(p => (p.DefName, p.Minimum)).Order(), round3.Pieces.Select(p => (p.DefName, p.Minimum)).Order());
        var ops3 = Owed(round3, bed, lamp, dresser, spot, endStanding);
        NoRemoval(ops3);
        Assert.DoesNotContain(ops3, o => o.Kind is OpKind.Build or OpKind.Install);
    }

    [Fact]
    public void AStandingBedStaysListedWhereItStandsAndNothingIsAbsent()
    {
        var room = Room();
        var interior = Interior(room);
        var fresh = RoomFurnishingRules.RoomFurnishing(room, interior, [BedWant], []);
        var furnishing = RoomFurnishingRules.RoomFurnishing(room, interior, [BedWant], [StandingAt(fresh.Pieces[0])]);
        var piece = Assert.Single(furnishing.Pieces);
        Assert.Equal(fresh.Pieces[0].Minimum, piece.Minimum);
        Assert.Equal(fresh.Pieces[0].Maximum, piece.Maximum);
        Assert.False(furnishing.Absent);
    }

    [Fact]
    public void ALeverPieceStandingStaysListedAndTheTemplateEntryKeepsItsSlot()
    {
        var room = Room();
        var interior = Interior(room);
        var lever = new FurnishingWant { Things = [Lever], Count = 1, Piece = BedShape with { Def = Lever } };
        var fresh = RoomFurnishingRules.RoomFurnishing(room, interior, [BedWant], []);
        var sculpture = Standing(Lever, 14, 14);
        var furnishing = RoomFurnishingRules.RoomFurnishing(room, interior, [BedWant, lever], [sculpture]);
        Assert.Contains(furnishing.Pieces, p => p.DefName == Lever && p.Minimum == new Cell(14, 14));
        Assert.Contains(fresh.Pieces[0], furnishing.Pieces);
    }

    [Fact]
    public void TheListIsStableAcrossRoundsAndBuildingOnePieceLeavesTheOthersAlone()
    {
        var room = Room();
        var interior = Interior(room);
        var lever = new FurnishingWant { Things = [Lever], Count = 1, Piece = BedShape with { Def = Lever } };
        var wants = new[] { BedWant, lever };
        var sculpture = Standing(Lever, 14, 14);
        var before = RoomFurnishingRules.RoomFurnishing(room, interior, wants, [sculpture]);
        Assert.Equal(before.Pieces, RoomFurnishingRules.RoomFurnishing(room, interior, wants, [sculpture]).Pieces);
        var bed = before.Pieces.Single(p => p.DefName == Bed);
        var after = RoomFurnishingRules.RoomFurnishing(room, interior, wants, [sculpture, StandingAt(bed)]);
        Assert.Equal(before.Pieces.Count, after.Pieces.Count);
        Assert.Equal(before.Pieces.Where(p => p.DefName != Bed), after.Pieces.Where(p => p.DefName != Bed));
        Assert.Equal(bed.Minimum, after.Pieces.Single(p => p.DefName == Bed).Minimum);
    }
}

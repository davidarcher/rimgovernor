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
        var expected = SleepingRules.BedroomTemplate(room, Shapes, Bed);
        Assert.NotNull(expected);
        var furnishing = RoomFurnishingRules.RoomFurnishing(room, Interior(room), [BedWant], []);
        Assert.Equal(expected, furnishing.Pieces);
        Assert.True(furnishing.Absent);
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

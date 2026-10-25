using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class RoomFurnishingTests
{
    private const string Bed = "Bed";
    private const string Lever = "SculptureSmall";

    private static readonly InteriorPieceDef BedShape = new(Bed, new Cell(1, 2), RoomRole.Bedroom, new InteractionCell.None());

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
                    ["EndTable"] = new("EndTable", new Cell(1, 1), RoomRole.None, new InteractionCell.None()),
                    ["Dresser"] = new("Dresser", new Cell(1, 1), RoomRole.None, new InteractionCell.None()),
                    [lamp] = new(lamp, new Cell(1, 1), RoomRole.None, new InteractionCell.None()),
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
        DoorSide = new DoorSide.Doored(Rotation.South),
    };

    private static InteriorRoom Interior(PlannedRoom room)
    {
        var (interior, ok) = InteriorRules.InteriorRoomFromLayout(room, Shapes);
        Assert.True(ok);
        return interior!;
    }

    private static FurnishingWant BedWant => new() { Key = "bed", Things = [Bed], Count = 1, Piece = BedShape };

    private static CurrentBuilding Standing(string def, int x, int z, int width = 1, int height = 1) => ConstructionObservationFixtures.Built(new Building(def, new Cell(x, z), Rotation.North, ""), id: $"{def}{x}_{z}", cells: [.. Enumerable.Range(0, width).SelectMany(dx => Enumerable.Range(0, height).Select(dz => new Cell(x + dx, z + dz)))]);

    private static CurrentBuilding StandingAt(WantedPiece p) =>
        Standing(p.DefName, p.Minimum.X, p.Minimum.Z, p.Maximum.X - p.Minimum.X + 1, p.Maximum.Z - p.Minimum.Z + 1);

    private static PieceShapes HospitalShapes => new()
    {
        Defs = new Dictionary<string, InteriorPieceDef>
        {
            [Bed] = BedShape,
            ["VitalsMonitor"] = new("VitalsMonitor", new Cell(1, 1), RoomRole.Hospital, new InteractionCell.None()),
            ["AnimalBed"] = new("AnimalBed", new Cell(1, 1), RoomRole.None, new InteractionCell.None()),
        },
        Furniture = new RoomFurniture
        {
            Beds = [new FurnitureBed { Def = Bed, Slots = 1 }],
            Monitor = new FacilityLink { Def = "VitalsMonitor" },
        },
    };

    private static PlannedRoom HospitalRoom() => new()
    {
        Id = 2,
        Role = LayoutSurveyRules.PlannedHospital,
        Region = RegionRules.RectRegion(new Rectangle { X = 10, Z = 10, Width = 7, Height = 5 }),
        Door = new Cell(12, 9),
        DoorSide = new DoorSide.Doored(Rotation.South),
    };

    [Fact]
    public void AHospitalListsEachStandingPieceOnceWhereItStandsWithAWard()
    {
        var room = HospitalRoom();
        var (interior, ok) = InteriorRules.InteriorRoomFromLayout(room, HospitalShapes);
        Assert.True(ok);
        var wants = RoomFurnishingRules.FacilityWants(interior!);
        Assert.Contains(wants, w => w.Things.Contains(Bed));
        Assert.Contains(wants, w => w.Things.Contains("VitalsMonitor"));
        var planned = InteriorRegionRules.PlanRoomInterior(interior!, new InteriorPieceDef()).Plan.Pieces;
        var hospitalBed = planned.First(p => p.Def == Bed);
        var monitor = planned.First(p => p.Def == "VitalsMonitor");
        var built = new[]
        {
            Standing(Bed, hospitalBed.Rect.X, hospitalBed.Rect.Z, hospitalBed.Rect.Width, hospitalBed.Rect.Height),
            Standing("VitalsMonitor", monitor.Rect.X, monitor.Rect.Z),
        };

        // The same defs as the ward's bed: the one standing bed is listed once.
        var both = HerdWardRules.HospitalFurnishing(room, HospitalShapes, BedShape, 1, built);
        Assert.Equal(1, both.Pieces.Count(p => p.DefName == Bed));
        Assert.Equal(1, both.Pieces.Count(p => p.DefName == "VitalsMonitor"));
        Assert.Equal(both.Pieces.Count, both.Pieces.Select(p => (p.DefName, p.Minimum)).Distinct().Count());

        // A facility list alone names the standing pieces and asks for nothing.
        var alone = RoomFurnishingRules.RoomFurnishing(room, null, wants, built);
        Assert.Equal(2, alone.Pieces.Count);
        Assert.False(alone.Absent);
    }

    [Fact]
    public void AFreshBedroomListsTheBedroomTemplateBed()
    {
        var room = Room();
        var furnishing = SleepingRules.BedroomFurnishing(room, Shapes, Bed, [], null);
        var bed = Assert.Single(furnishing.Pieces);
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
            var input = new ReconcileInput { Rooms = RoomTestFacts.NoRooms, Plan = plan, Room = room, Ground = new GroundCensus(), Rows = ReconcileRules.OwnerRows(room, rows, furnishing.Pieces, null, _ => false), Furniture = furnishing.Pieces };
            return [.. ReconcileRules.Reconcile(input).Owed];
        }

        void NoRemoval(IEnumerable<Operation> ops) =>
            Assert.DoesNotContain(ops, o => o.Kind is OpKind.Pack or OpKind.PackInUse or OpKind.FurnitureOut);

        // Round 1: the lever pieces stand and no lever asks for more: every piece is listed where it stands, nothing is owed.
        var round1 = SleepingRules.BedroomFurnishing(room, Shapes, "", [bed, lamp, dresser, spot], null);
        Assert.Equal(4, round1.Pieces.Count);
        Assert.False(round1.Absent);
        var ops1 = Owed(round1, bed, lamp, dresser, spot);
        NoRemoval(ops1);
        Assert.DoesNotContain(ops1, o => o.Kind is OpKind.Build or OpKind.Install);

        // Round 2: a lever asks for an end table: the standing pieces stay listed and only the end table is built.
        var round2 = SleepingRules.BedroomFurnishing(room, Shapes, "", [bed, lamp, dresser, spot], endTable);
        Assert.True(round2.Absent);
        Assert.Equal(5, round2.Pieces.Count);
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
    public void AReplacementBedIsBuiltInTheLeversStuffBesideTheOldBedAndASculptureIsNeverPacked()
    {
        var room = Room();
        var plan = new LayoutPlan { Rooms = [room] };
        var old = StandingAt(SleepingRules.BedroomFurnishing(room, Shapes, Bed, [], null).Pieces.Single());
        var free = room.Region.Cells().Where(c => !old.Cells.Contains(c)).ToList();
        var sculpture = Standing(Lever, free[0].X, free[0].Z);
        var open = free.Skip(1).Where(c => free.Skip(1).Contains(new Cell(c.X, c.Z + 1))).First();
        var replacement = new BedReplacement { Step = BedReplacementStep.Build, Room = "1", Def = Bed, Stuff = "Steel", Cell = open, Rot = Rotation.North }.Piece();
        Assert.Equal(new Cell(1, 2), replacement.Size);

        var furnishing = SleepingRules.BedroomFurnishing(room, Shapes, "", [old, sculpture], replacement);
        Assert.True(furnishing.Absent);
        Assert.Contains(furnishing.Pieces, p => p.DefName == Bed && p.Minimum == old.Cells[0]);
        var asked = furnishing.Pieces.Single(p => p.DefName == Bed && p.Minimum != old.Cells[0]);
        Assert.Equal("Steel", asked.Stuff);

        var rows = new[] { old, sculpture }.Select(b => new ClearanceTarget
        {
            EntityId = b.Id,
            DefName = b.Building!.Definition,
            Minimum = b.Cells[0],
            Maximum = b.Cells[^1],
            Player = true,
            Packable = true,
        }).ToList();
        var input = new ReconcileInput
        {
            Rooms = RoomTestFacts.NoRooms,
            Plan = plan,
            Room = room,
            Ground = new GroundCensus(),
            Rows = ReconcileRules.OwnerRows(room, rows, furnishing.Pieces, null, def => def == Lever),
            Furniture = furnishing.Pieces,
            Stock = new Dictionary<string, int> { [Bed] = 1 },
        };
        var ops = ReconcileRules.Reconcile(input).Owed;
        Assert.DoesNotContain(ops, o => o.Kind is OpKind.Pack or OpKind.PackInUse or OpKind.FurnitureOut or OpKind.Install);
        Assert.Equal(Bed, ops.Single(o => o.Kind == OpKind.Build).Pieces.Single().DefName);
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

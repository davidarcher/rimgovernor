using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Owners reconcile against the full furniture list: unlisted interior furniture is stale and removed (#3487).</summary>
public sealed class StaleFurnitureTests
{
    private const string Stray = "Dresser";

    private static readonly Func<string, bool> NothingCarried = _ => false;

    private static PlannedRoom Room(PlannedRole role = PlannedRole.Bedroom) => new()
    {
        Id = 1,
        Role = role,
        Region = RegionRules.RectRegion(new Rectangle { X = 10, Z = 10, Width = 5, Height = 5 }),
        Door = new Cell(12, 9),
        DoorRot = Rotation.South,
    };

    private static ClearanceTarget Row(string id, string def, Cell cell, Cell? max = null) => new()
    {
        EntityId = id,
        DefName = def,
        Minimum = cell,
        Maximum = max ?? cell,
        Player = true,
        Packable = true,
        Deconstructible = true,
    };

    private static WantedPiece Listed(string def, Cell cell) =>
        new() { DefName = def, Minimum = cell, Maximum = cell, Size = new Cell(1, 1), Rot = Rotation.North };

    private static readonly OpKind[] RemovalKinds = [OpKind.FurnitureOut, OpKind.Pack, OpKind.PackInUse];

    /// <summary>The ids the owner's reconcile of the room removes, given every census row the room holds.</summary>
    private static List<string> OwnerRemovals(PlannedRoom room, IReadOnlyList<ClearanceTarget> rows, IReadOnlyList<WantedPiece> template, Func<string, bool> carried, Func<string, bool>? forbidden = null)
    {
        var input = new ReconcileInput
        {
            Rooms = RoomObservation.NoRooms,
            Plan = new LayoutPlan { Rooms = [room] },
            Room = room,
            Ground = new GroundCensus(),
            Rows = ReconcileRules.OwnerRows(room, rows, template, forbidden, carried),
            Furniture = template,
        };
        return [.. ReconcileRules.Reconcile(input).Owed.Where(o => RemovalKinds.Contains(o.Kind)).SelectMany(o => o.Targets).Select(t => t.EntityId)];
    }

    [Fact]
    public void AnUnlistedInteriorPieceIsRemovedAndListedPiecesStay()
    {
        var room = Room();
        var template = new[] { Listed("Bed", new Cell(11, 11)) };
        var rows = new[] { Row("bed", "Bed", new Cell(11, 11)), Row("stale", Stray, new Cell(13, 13)) };
        Assert.Equal(["stale"], OwnerRemovals(room, rows, template, NothingCarried));
    }

    [Fact]
    public void ADuplicateOfAListedPieceAtAnotherCellIsStale()
    {
        var room = Room();
        var template = new[] { Listed(Stray, new Cell(11, 11)) };
        var rows = new[] { Row("a", Stray, new Cell(11, 11)), Row("b", Stray, new Cell(12, 12)) };
        Assert.Equal(["b"], OwnerRemovals(room, rows, template, NothingCarried));
    }

    [Fact]
    public void AnOwnerRowIsNeverListedTwice()
    {
        var room = Room();
        var template = new[] { Listed(Stray, new Cell(11, 11)) };
        var rows = new[] { Row("a", Stray, new Cell(11, 11)), Row("b", Stray, new Cell(12, 12)), Row("wall", "Wall", new Cell(9, 9)) with { EnclosesRoom = true } };
        var own = ReconcileRules.OwnerRows(room, rows, template, null, NothingCarried);
        Assert.Equal(own.Count, own.Select(r => r.EntityId).Distinct().Count());
        Assert.Equal(["a", "b"], own.Select(r => r.EntityId).Order());
    }

    public static TheoryData<string> CarriedDefs() => new()
    {
        "TorchLamp",
        "StandingLamp",
        PartySpotRules.PartySpotDefinition,
        RoomQualityRules.PlantPotDefinition,
        "SculptureSmall",
        "DiningChair",
        "DiningTable",
        "DiningPin",
        "WatchedTelevision",
        "BilliardsFoothold",
        "PowerConduit",
        "HiddenConduit",
        "Battery",
        "Vent",
        "Campfire",
        "PassiveCooler",
    };

    private static readonly string[] WatchOnly = ["WatchedTelevision"];

    private static Func<string, bool> Carried() => ReconcileRules.CarriedOver(
        ["TorchLamp", "StandingLamp"],
        new ComfortObservation
        {
            Furniture = new DiningFurniture
            {
                Chair = new InteriorPieceDef("DiningChair", new Cell(1, 1), RoomRole.None, null),
                Table = new InteriorPieceDef("DiningTable", new Cell(2, 1), RoomRole.None, null),
                Pin = new InteriorPieceDef("DiningPin", new Cell(1, 1), RoomRole.None, null),
            },
            WatchBuildings = EquatableArray.From(WatchOnly),
            RecreationFoothold = "BilliardsFoothold",
        },
        new ItemFacts { Sculptures = [new Sculpture("MakeSculptureSmall", "SculptureSmall", new Cell(1, 1), 1, 1)] }
    );

    [Theory]
    [MemberData(nameof(CarriedDefs))]
    public void EveryCarriedOverClassStaysAndAnUnlistedStrayBesideItIsRemoved(string def)
    {
        var room = Room();
        var rows = new[] { Row("carried", def, new Cell(11, 11)), Row("stray", Stray, new Cell(12, 12)) };
        Assert.True(Carried()(def));
        Assert.Equal(["stray"], OwnerRemovals(room, rows, [], Carried()));
        Assert.Equal(["carried", "stray"], OwnerRemovals(room, rows, [], NothingCarried).Order());
    }

    [Fact]
    public void ACarriedOverSetWithoutAComfortReadStillCarriesTheFixedDefinitions()
    {
        var carried = ReconcileRules.CarriedOver([], null, new ItemFacts());
        Assert.True(carried(PartySpotRules.PartySpotDefinition));
        Assert.True(carried(RoomQualityRules.PlantPotDefinition));
        Assert.False(carried(Stray));
    }

    [Fact]
    public void WallsDoorsFramesStandInBedsForbiddenDefsAndThingsOffTheInteriorAreNeverStale()
    {
        var room = Room();
        var rows = new[]
        {
            Row("wall", "Wall", new Cell(11, 11)) with { EnclosesRoom = true },
            Row("ancient", "AncientWall", new Cell(12, 11)) with { Class = "ancient_wall_door" },
            Row("frame", "Frame_Dresser", new Cell(13, 11)),
            Row("spot", "SleepingSpot", new Cell(11, 12)),
            Row("forbidden", "Brazier", new Cell(12, 12)),
            Row("ring", Stray, new Cell(9, 10)),
            Row("straddle", "Bed", new Cell(14, 14), new Cell(14, 15)),
            Row("foreign", Stray, new Cell(11, 13)) with { Player = false },
        };
        Assert.Empty(ReconcileRules.StaleInterior(room, rows, [], def => def == "Brazier", NothingCarried));
    }

    [Fact]
    public void ForbiddenDefsStillLeaveByTheOwnersReconcile()
    {
        var room = Room();
        var rows = new[] { Row("forbidden", "Brazier", new Cell(12, 12)) };
        Assert.Equal(["forbidden"], OwnerRemovals(room, rows, [], NothingCarried, def => def == "Brazier"));
    }

    [Fact]
    public void AMorguesInteriorFurnitureIsRemovedBecauseItsListIsEmpty()
    {
        var room = Room(PlannedRole.Morgue);
        var rows = new[] { Row("table", "Table2x2c", new Cell(11, 11), new Cell(12, 12)), Row("shelf", "Shelf", new Cell(14, 14)) };
        Assert.Equal(["shelf", "table"], OwnerRemovals(room, rows, [], NothingCarried).Order());
    }

    [Fact]
    public void ARangeTemplateKeepsItsListedPiecesAndRemovesAStrayInsideTheRange()
    {
        var range = PlannedRoom.RangeFromBlock(new Rectangle { X = 20, Z = 20, Width = 8, Height = 20 }, Rotation.North, 3);
        var template = PlannedRoom.RangeTemplate(range);
        Assert.NotEmpty(template);
        var rows = template.Select((p, i) => Row($"p{i}", p.DefName, p.Minimum)).ToList();
        Assert.Empty(OwnerRemovals(range, rows, template, NothingCarried));

        var free = range.Region.Cells().First(c => template.All(p => p.Minimum != c));
        rows.Add(Row("stray", Stray, free));
        Assert.Equal(["stray"], OwnerRemovals(range, rows, template, NothingCarried));
    }

    [Fact]
    public void ARingTemplateKeepsItsMarkersAndRemovesAStrayInsideTheRing()
    {
        var ring = PlannedRoom.RingFromBlock(new Rectangle { X = 20, Z = 20, Width = 11, Height = 11 }, 2);
        var template = PlannedRoom.RingTemplate(ring);
        Assert.Equal(2, template.Count);
        var rows = template.Select((p, i) => Row($"m{i}", p.DefName, p.Minimum)).ToList();
        Assert.Empty(OwnerRemovals(ring, rows, template, NothingCarried));

        rows.Add(Row("stray", Stray, ring.Region.Cells().First(c => template.All(p => p.Minimum != c))));
        Assert.Equal(["stray"], OwnerRemovals(ring, rows, template, NothingCarried));
    }

    [Fact]
    public void AStandingPenMarkerIsListedWhereItStandsAndIsNotStale()
    {
        var pen = Room(PlannedRole.Pen);
        var marker = new CurrentBuilding
        {
            Id = "marker",
            Building = new Building(HerdPenRules.PenMarkerDefinition, new Cell(12, 12), Rotation.North, ""),
            Cells = [new Cell(12, 12)],
        };
        var (piece, ok) = HerdPenRules.PenMarkerPiece(pen, new InteriorPieceDef(HerdPenRules.PenMarkerDefinition, new Cell(1, 1), RoomRole.None, null), [marker], new HashSet<Cell>());
        Assert.True(ok);
        var rows = new[] { Row("marker", HerdPenRules.PenMarkerDefinition, new Cell(12, 12)), Row("stray", Stray, new Cell(13, 13)) };
        Assert.Equal(["stray"], OwnerRemovals(pen, rows, [piece], NothingCarried));
    }

    [Fact]
    public void AListedLeverPieceIsNeverRemovedOverSeveralRounds()
    {
        var room = Room();
        var template = new List<WantedPiece> { Listed("Bed", new Cell(11, 11)), Listed("EndTable", new Cell(12, 11)), Listed("TorchLamp", new Cell(13, 11)) };
        var rows = template.Select((p, i) => Row($"p{i}", p.DefName, p.Minimum)).ToList();
        for (var round = 0; round < 6; round++)
        {
            Assert.Empty(OwnerRemovals(room, rows, template, Carried()));
        }

        // With the lamp unlisted it is still carried over, never removed; the end table, no longer listed, is stale.
        template.RemoveAt(1);
        Assert.Equal(["p1"], OwnerRemovals(room, rows, template, Carried()));
    }

    private static CurrentBuilding Built(string def, int x, int z, int width = 1, int height = 1) => new()
    {
        Id = $"{def}{x}_{z}",
        Building = new Building(def, new Cell(x, z), Rotation.North, ""),
        Cells = [.. Enumerable.Range(0, width).SelectMany(dx => Enumerable.Range(0, height).Select(dz => new Cell(x + dx, z + dz)))],
    };

    private static List<ClearanceTarget> RowsOf(params CurrentBuilding[] standing) =>
        [.. standing.Select(b => Row(b.Id, b.Building!.Definition, b.Cells[0], b.Cells[^1]))];

    [Fact]
    public void ABedroomOwnerListsEveryStandingBedSoAnUpgradeDoesNotRemoveTheBed()
    {
        var room = Room();
        var shapes = new PieceShapes
        {
            Defs = new Dictionary<string, InteriorPieceDef>
            {
                ["Bed"] = new("Bed", new Cell(1, 2), RoomRole.Bedroom, null),
                ["Bedroll"] = new("Bedroll", new Cell(1, 2), RoomRole.None, null),
                ["EndTable"] = new("EndTable", new Cell(1, 1), RoomRole.None, null),
            },
            Furniture = new RoomFurniture
            {
                Beds = [new FurnitureBed { Def = "Bed", Slots = 1 }, new FurnitureBed { Def = "Bedroll", Slots = 1 }],
                EndTable = new FacilityLink { Def = "EndTable" },
            },
        };
        var bed = Built("Bed", 11, 11, 1, 2);
        var other = Built("Bedroll", 13, 11, 1, 2);
        var stale = Built(Stray, 12, 14);
        var standing = new[] { bed, other, stale };

        // The room-quality upgrade path asks for no bed (bed ""), only the lever's piece; the beds stand and are listed.
        var upgrade = SleepingRules.BedroomFurnishing(room, shapes, "", standing, null);
        Assert.Contains(upgrade.Pieces, p => p.DefName == "Bed");
        Assert.Contains(upgrade.Pieces, p => p.DefName == "Bedroll");
        Assert.Equal([stale.Id], OwnerRemovals(room, RowsOf(standing), upgrade.Pieces, NothingCarried));

        // The bedroom path asks for the chosen bed: the standing one is listed once and nothing is absent.
        var chosen = SleepingRules.BedroomFurnishing(room, shapes, "Bed", [bed], null);
        Assert.Single(chosen.Pieces, p => p.DefName == "Bed");
        Assert.False(chosen.Absent);
    }

    [Fact]
    public void AJailListsItsStandingBedsBesideTheOneOwedSoOnlyTheFreshSlotIsBuilt()
    {
        var prison = Room(PlannedRole.Prison);
        var shapes = new PieceShapes
        {
            Defs = new Dictionary<string, InteriorPieceDef> { ["Bed"] = new("Bed", new Cell(1, 2), RoomRole.Bedroom, null) },
            Furniture = new RoomFurniture { Beds = [new FurnitureBed { Def = "Bed", Slots = 1 }] },
        };
        var standing = new[] { Built("Bed", 10, 10, 1, 2), Built("Bed", 12, 10, 1, 2) };
        var furnishing = JailRules.JailFurnishing(prison, shapes, standing);
        Assert.True(furnishing.Absent);
        Assert.Equal(3, furnishing.Pieces.Count(p => p.DefName == "Bed"));
        Assert.All(standing, b => Assert.Contains(furnishing.Pieces, p => p.DefName == "Bed" && p.Minimum == b.Cells[0]));
        Assert.Empty(OwnerRemovals(prison, RowsOf(standing), furnishing.Pieces, NothingCarried));
    }

    [Fact]
    public void ATombListsItsStandingSarcophagiBesideTheOneOwedSoOnlyTheFreshSlotIsBuilt()
    {
        var tomb = Room(PlannedRole.Tomb);
        var shapes = new PieceShapes
        {
            Defs = new Dictionary<string, InteriorPieceDef> { ["Sarcophagus"] = new("Sarcophagus", new Cell(1, 2), RoomRole.None, null) },
            Furniture = new RoomFurniture { Sarcophagus = "Sarcophagus" },
        };
        var standing = new[] { Built("Sarcophagus", 10, 10, 1, 2), Built("Sarcophagus", 12, 10, 1, 2) };
        var step = TombRules.NextTombStep(new LayoutPlan { Rooms = [tomb], NextRoomId = 2 }, Enumerable.Repeat(new WasteItem { CorpseOf = CorpseOf.Colonist }, 3).ToArray(), standing, shapes, true);
        Assert.Equal(TombStepKind.Reconcile, step.Kind);
        Assert.Equal(3, step.Template.Count(p => p.DefName == "Sarcophagus"));
        Assert.Empty(OwnerRemovals(tomb, RowsOf(standing), [.. step.Template], NothingCarried));
    }

    [Fact]
    public void AFacilityListNamesEveryStandingPieceOfItsDefinitionsNotOnlyThePlansCount()
    {
        var room = Room(PlannedRole.Hospital);
        var shapes = new PieceShapes
        {
            Defs = new Dictionary<string, InteriorPieceDef> { ["Bed"] = new("Bed", new Cell(1, 2), RoomRole.Bedroom, null), ["VitalsMonitor"] = new("VitalsMonitor", new Cell(1, 1), RoomRole.None, null) },
            Furniture = new RoomFurniture { Beds = [new FurnitureBed { Def = "Bed", Slots = 1 }], Monitor = new FacilityLink { Def = "VitalsMonitor" } },
        };
        var (inner, ok) = InteriorRules.InteriorRoomFromLayout(room, shapes);
        Assert.True(ok);
        var wants = RoomFurnishingRules.FacilityWants(inner!);
        var def = Assert.Single(wants, w => w.Things.Contains("Bed"));
        var many = Enumerable.Range(0, 4).Select(i => Built("Bed", 10 + i, 10, 1, 2)).ToArray();
        var listed = RoomFurnishingRules.RoomFurnishing(room, null, wants, many);
        Assert.Equal(many.Length, listed.Pieces.Count(p => p.DefName == "Bed"));
        Assert.False(listed.Absent);
        Assert.NotNull(def);
    }

    [Fact]
    public void TheClearSideAndTheOwnerNeverTargetTheSamePieceInARound()
    {
        var room = Room();
        var template = new[] { Listed("Bed", new Cell(11, 11)) };
        var rows = new[]
        {
            Row("bed", "Bed", new Cell(11, 11)),
            Row("stale", Stray, new Cell(13, 13)),
            Row("onRing", Stray, new Cell(9, 10)),
        };
        // The ring is unfinished (an empty ground census): the clear side runs.
        var plan = new LayoutPlan { Rooms = [room] };
        var clear = ClearanceReconcileRules.PlannedGroundStep(plan, new GroundCensus(), rows, [], RoomObservation.NoRooms, new RetiredGround(), null);
        var clearIds = clear?.Targets.Items.Select(t => t.EntityId).ToHashSet() ?? [];
        var ownerIds = OwnerRemovals(room, rows, template, NothingCarried).ToHashSet();

        Assert.Equal(["stale"], ownerIds);
        Assert.Equal(["onRing"], clearIds);
        Assert.Empty(clearIds.Intersect(ownerIds));
    }
}

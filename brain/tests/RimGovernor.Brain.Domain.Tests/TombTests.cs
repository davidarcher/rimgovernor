using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The tomb's sarcophagus step: a dead colonist is staged into a free tomb-room slot, and a stranger is owed
/// nothing (no haul grants KnowBuriedInSarcophagus, #3242), so it keeps the morgue path.
/// </summary>
public sealed class TombTests
{
    private const string Sarcophagus = "Sarcophagus";

    private static readonly PieceShapes Shapes = new()
    {
        Defs = new Dictionary<string, InteriorPieceDef> { [Sarcophagus] = new(Sarcophagus, new Cell(1, 2), RoomRole.None, null) },
        Furniture = new RoomFurniture { Sarcophagus = Sarcophagus },
    };

    private static readonly PlannedRoom TombRoom = new()
    {
        Id = 1,
        Role = PlannedRole.Tomb,
        Region = RectRegion(new Rectangle { X = 10, Z = 10, Width = 5, Height = 4 }),
        Door = new Cell(12, 9),
        DoorRot = Rotation.South,
    };

    private static readonly LayoutPlan Plan = new() { Rooms = [TombRoom], NextRoomId = 2 };

    private static WasteItem Corpse(string id, CorpseOf of) =>
        new() { Id = id, Kind = "Corpse", State = WasteState.Exposed, Eligible = true, Cell = new Cell(30, 30), CorpseOf = of };

    private static WasteItem Buried(string id, CorpseOf of, string grave) =>
        new() { Id = id, Kind = "Corpse", State = WasteState.Buried, CorpseOf = of, Grave = grave };

    private static CurrentBuilding Standing(string id, int x, int z) => new()
    {
        Id = id,
        Building = new Building(Sarcophagus, new Cell(x, z), Rotation.North, "WoodLog"),
        Cells = [new Cell(x, z), new Cell(x, z + 1)],
    };

    private static Fact<bool> Owed(params WasteItem[] waste) =>
        TombOwed(Shapes, Known(true), Known(Plan), Known(new RoomObservation()), Known<EquatableArray<WasteItem>>([.. waste]), Known(new CurrentConstruction { Colony = true }));

    [Fact]
    public void AColonistCorpseIsStagedIntoAFreeTombSlot()
    {
        var step = NextTombStep(Plan, [Corpse("ann", CorpseOf.Colonist)], [], Shapes, true);
        Assert.Equal(TombStepKind.Reconcile, step.Kind);
        Assert.Equal(TombRoom.Id, step.Room.Id);
        Assert.Equal(Sarcophagus, Assert.Single(step.Template).DefName);
        Assert.Equal(1, step.Dead);
        Assert.True(Owed(Corpse("ann", CorpseOf.Colonist)).TryGetValue(out var o) && o);
    }

    [Fact]
    public void AStrangerCorpseIsOwedNoTomb()
    {
        Assert.Equal(TombStepKind.None, NextTombStep(Plan, [Corpse("raider", CorpseOf.Stranger)], [], Shapes, true).Kind);
        Assert.True(Owed(Corpse("raider", CorpseOf.Stranger)).TryGetValue(out var o) && !o);
    }

    [Fact]
    public void ABuriedStrangerFillsItsSarcophagus()
    {
        var built = new[] { Standing("s1", 10, 11) };
        var step = NextTombStep(Plan, [Buried("raider", CorpseOf.Stranger, "s1"), Corpse("ann", CorpseOf.Colonist)], built, Shapes, true);
        Assert.Equal(TombStepKind.Reconcile, step.Kind);
        Assert.Equal(0, step.Empty);
    }
}

using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>The throne step's template is the RoomFurnishing list (#3483): the same pieces the hand-built template listed.</summary>
public sealed class ThroneFurnishingTests
{
    private const string Throne = "Throne";
    private const string Brazier = "Brazier";
    private const string Column = "Column";

    private static readonly ThroneNeed Need = new(
        new ThroneRequirements
        {
            Things = [Throne],
            MinArea = 30,
            AnyOfCounts = [new ThingAnyOfCount { Things = [Brazier], Count = 2 }],
            Counts = [new ThingCount { Def = Column, Count = 2 }],
        }
    );

    private static readonly FurnitureDefinition[] Defs =
    [
        Definition(Throne, 1, 1),
        Definition(Brazier, 1, 1),
        Definition(Column, 1, 1),
    ];

    private static PieceShapes Shapes
    {
        get
        {
            var lamp = InteriorRegionRules.InteriorDefs().LampDef;
            return new()
            {
                Defs = new Dictionary<string, InteriorPieceDef>
                {
                    ["Bed"] = new("Bed", new Cell(1, 2), RoomRole.Bedroom, new InteractionCell.None()),
                    ["EndTable"] = new("EndTable", new Cell(1, 1), RoomRole.None, new InteractionCell.None()),
                    ["Dresser"] = new("Dresser", new Cell(1, 1), RoomRole.None, new InteractionCell.None()),
                    [lamp] = new(lamp, new Cell(1, 1), RoomRole.None, new InteractionCell.None()),
                },
                Furniture = new RoomFurniture
                {
                    Beds = [new FurnitureBed { Def = "Bed", Slots = 1 }],
                    EndTable = new FacilityLink { Def = "EndTable" },
                    Dresser = new FacilityLink { Def = "Dresser" },
                },
            };
        }
    }

    private static FurnitureDefinition Definition(string name, int w, int h) => new() { Name = name, Available = Known(true), Size = Known(new Bounds(w, h)) };

    private static PlannedRoom Room() => new()
    {
        Id = 1,
        Role = PlannedRole.Throne,
        Region = RegionRules.RectRegion(new Rectangle { X = 10, Z = 10, Width = 7, Height = 6 }),
        Door = new Cell(13, 9),
        DoorSide = new DoorSide.Doored(Rotation.South),
    };

    private static CurrentBuilding Standing(WantedPiece p) => new()
    {
        Id = $"{p.DefName}{p.Minimum.X}_{p.Minimum.Z}",
        Building = new Building(p.DefName, p.Minimum, Rotation.North, ""),
        Cells = [p.Minimum],
    };

    private static ThroneStep Step(IReadOnlyList<CurrentBuilding> built) =>
        ThroneRules.NextThroneStep(new LayoutPlan { Rooms = [Room()] }, RoomTestFacts.NoRooms with { Shapes = Shapes }, new GroundCensus(), built, Need, Defs, []);

    /// <summary>The template as the hand-built loops listed it: the throne slot, then each requirement's slots in want order.</summary>
    private static List<WantedPiece> HandBuilt(IReadOnlyList<CurrentBuilding> built)
    {
        var (inRoom, ok) = InteriorRules.InteriorRoomFromLayout(Room(), Shapes);
        Assert.True(ok);
        var def = new InteriorPieceDef(Throne, new Cell(1, 1), default, new InteractionCell.None());
        var required = new List<RequiredPiece>(inRoom!.Required);
        required.AddRange(Enumerable.Range(0, 2).Select(i => new RequiredPiece($"anyofcount0.{i}", new InteriorPieceDef(Brazier, new Cell(1, 1), default, new InteractionCell.None()))));
        required.AddRange(Enumerable.Range(0, 2).Select(i => new RequiredPiece($"count0.{i}", new InteriorPieceDef(Column, new Cell(1, 1), default, new InteractionCell.None()))));
        var (interior, planned) = InteriorRegionRules.PlanRoomInterior(inRoom with { Required = required }, def);
        Assert.True(planned);
        var taken = built.SelectMany(b => b.Cells).ToHashSet();
        var pieces = new List<WantedPiece>();
        if (built.FirstOrDefault(b => b.Building!.Definition == Throne) is { } standing)
        {
            pieces.Add(new WantedPiece { DefName = Throne, Minimum = standing.Cells[0], Maximum = standing.Cells[0] });
        }
        else
        {
            pieces.Add(interior.Pieces.First(p => p.Slot == "throne" && p.Def == Throne).Wanted());
        }

        foreach (var (key, thing, count) in new[] { ("anyofcount0", Brazier, 2), ("count0", Column, 2) })
        {
            var standingOnes = built.Where(b => b.Building!.Definition == thing).Take(count).Select(b => new WantedPiece { DefName = thing, Minimum = b.Cells[0], Maximum = b.Cells[0] }).ToList();
            pieces.AddRange(standingOnes);
            pieces.AddRange(interior.Pieces.Where(p => p.Slot.StartsWith(key + ".", StringComparison.Ordinal) && !taken.Contains(new Cell(p.Rect.X, p.Rect.Z))).Take(count - standingOnes.Count).Select(p => p.Wanted()));
        }

        return pieces;
    }

    [Fact]
    public void AFreshThroneRoomListsTheThroneAndEveryRequiredPiece()
    {
        var step = Step([]);
        Assert.Equal(ThroneStepKind.Reconcile, step.Kind);
        Assert.Equal(5, step.Template.Count);
        Assert.Equal(HandBuilt([]), step.Template);
    }

    [Fact]
    public void AStandingThroneAndBrazierStayWhereTheyStandAndTheRestStayListed()
    {
        var fresh = Step([]).Template;
        var built = new[] { Standing(fresh[0]), Standing(fresh[1]) };
        var step = Step(built);
        Assert.Equal(HandBuilt(built), step.Template);
        Assert.Equal(5, step.Template.Count);
        Assert.Equal(fresh[0].Minimum, step.Template[0].Minimum);
        Assert.Equal(fresh[1].Minimum, step.Template[1].Minimum);
    }
}

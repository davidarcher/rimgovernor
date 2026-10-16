using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3229: a plain grave is sited on open ground near the base, not in a planned graveyard room. GraveSites keeps it
/// off the colonists' routes, the crop fields, the food stores and the planned ground, and ranks by walking distance
/// from the base's entrance; the tomb step owes a grave from the unburied dead alone.
/// </summary>
public sealed class GraveSitingTests
{
    private const int Size = 30;

    // The storeroom's interior is 10..14 x 10..14; its door opens south onto the entrance cell.
    private static readonly PlannedRoom Storeroom = new()
    {
        Id = 1,
        Role = PlannedRole.Storage,
        Region = RectRegion(new Rectangle { X = 10, Z = 10, Width = 5, Height = 5 }),
        Door = new Cell(12, 9),
        DoorRot = Rotation.South,
    };

    private static readonly Cell Entrance = new(12, 8);

    private static readonly LayoutPlan Plan = new() { Rooms = [Storeroom], Entrances = [Entrance], NextRoomId = 2 };

    private static readonly LightTerrains Light = new(new Dictionary<string, bool> { ["Soil"] = true });

    // A crop field west of the entrance and a food stockpile east of it, both zones in the census.
    private static readonly Rectangle Crops = new() { X = 2, Z = 2, Width = 8, Height = 6 };

    private static readonly Rectangle FoodStore = new() { X = 15, Z = 2, Width = 4, Height = 4 };

    // The colonists' path from the entrance south to the map edge.
    private static readonly IReadOnlyList<Cell> Traffic = [.. Enumerable.Range(0, 8).Select(z => new Cell(12, z))];

    private static bool In(Rectangle r, Cell c) => c.X >= r.X && c.X < r.X + r.Width && c.Z >= r.Z && c.Z < r.Z + r.Height;

    private static IEnumerable<Cell> CellsOf(Rectangle r) =>
        from x in Enumerable.Range(r.X, r.Width) from z in Enumerable.Range(r.Z, r.Height) select new Cell(x, z);

    private static List<SiteCell> Census(Func<Cell, bool>? walkable = null)
    {
        var cells = new List<SiteCell>();
        for (var x = 0; x < Size; x++)
        {
            for (var z = 0; z < Size; z++)
            {
                var c = new Cell(x, z);
                var zoned = In(Crops, c) || In(FoodStore, c);
                cells.Add(new SiteCell { Cell = c, Walkable = Known(walkable?.Invoke(c) ?? true), Zone = Known(zoned), Terrain = Known("Soil") });
            }
        }
        return cells;
    }

    private static GraveSiteInput Input(List<SiteCell> cells) => new() { Bounds = new Bounds(Size, Size), Cells = cells, Light = Light, Plan = Plan, Traffic = Traffic };

    [Fact]
    public void GravesStayOffRoutesCropsFoodStoresAndPlannedGround()
    {
        var sites = GraveSites(Input(Census()));
        Assert.NotEmpty(sites);
        // The interior, its wall ring and the one-cell margin.
        var planned = new Rectangle { X = 8, Z = 8, Width = 9, Height = 9 };
        foreach (var cell in sites.SelectMany(CellsOf))
        {
            Assert.DoesNotContain(cell, Traffic);
            Assert.False(In(Crops, cell), $"grave on the crop field at {cell}");
            Assert.False(In(FoodStore, cell), $"grave on the food store at {cell}");
            Assert.False(In(planned, cell), $"grave on planned ground at {cell}");
            Assert.True(Math.Abs(cell.X - Entrance.X) > 1 || Math.Abs(cell.Z - Entrance.Z) > 1, $"grave beside the entrance at {cell}");
        }
    }

    [Fact]
    public void GravesAreRankedByWalkingDistanceFromTheEntrance()
    {
        var cells = Census();
        var sites = GraveSites(Input(cells));
        var costs = HaulCosts(cells, [new HaulConsumer { Cells = [Entrance], Weight = 1 }]);
        var walked = sites.Select(s => CellsOf(s).Min(c => costs[c])).ToList();
        Assert.Equal(walked.Order(), walked);
        Assert.True(walked[0] <= 3, $"the nearest grave is {walked[0]} steps from the entrance");
    }

    [Fact]
    public void AGroundPocketNoEntranceReachesTakesNoGrave()
    {
        // A wall ring at x 20..26, z 20..26 seals the pocket inside it.
        static bool Ring(Cell c) => c.X is >= 20 and <= 26 && c.Z is >= 20 and <= 26 && (c.X is 20 or 26 || c.Z is 20 or 26);
        static bool Pocket(Cell c) => c.X is > 20 and < 26 && c.Z is > 20 and < 26;
        var sites = GraveSites(Input(Census(c => !Ring(c))));
        Assert.DoesNotContain(sites.SelectMany(CellsOf), Pocket);
    }

    [Fact]
    public void NoPlanNoSites() =>
        Assert.Empty(GraveSites(Input(Census()) with { Plan = new LayoutPlan() }));

    [Fact]
    public void WithoutASarcophagusTheDeadOweAGraveAndNoRoom()
    {
        var corpse = new WasteItem { Id = "ann", Kind = "Corpse", State = WasteState.Exposed, Eligible = true, Cell = new Cell(5, 5), CorpseOf = CorpseOf.Colonist };
        var shapes = new PieceShapes { Furniture = new RoomFurniture { Sarcophagus = "Sarcophagus" } };
        var step = NextTombStep(Plan, [corpse], [], shapes, false, new StrangerTomb());
        Assert.Equal(TombStepKind.Grave, step.Kind);
        Assert.Equal(0, step.Room.Id);
        Assert.Empty(step.Template);

        var empty = new CurrentBuilding { Id = "g1", Building = new Building(GraveDefinition, new Cell(3, 20), Rotation.North, ""), Cells = [new Cell(3, 20), new Cell(3, 21)] };
        Assert.Equal(TombStepKind.None, NextTombStep(Plan, [corpse], [empty], shapes, false, new StrangerTomb()).Kind);
    }
}

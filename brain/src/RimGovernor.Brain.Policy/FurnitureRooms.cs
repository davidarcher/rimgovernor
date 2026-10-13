namespace RimGovernor.Brain.Policy;

/// <summary>One installed building inside a room: its thing id, its definition, its North size, rotation and world footprint. (Go <c>FurniturePiece</c>.)</summary>
public sealed record FurniturePiece
{
    public string Thing { get; init; } = "";

    public string Def { get; init; } = "";

    public Cell Size { get; init; }

    public Rotation Rot { get; init; }

    public Rectangle Rect { get; init; }
}

/// <summary>A room the furniture levers measure: its census id, its plan input and every building standing on its floor. (Go <c>FurnitureRoom</c>.)</summary>
public sealed record FurnitureRoom
{
    public string Id { get; init; } = "";

    public InteriorRoom Room { get; init; } = new();

    public IReadOnlyList<FurniturePiece> Pieces { get; init; } = [];

    /// <summary>
    /// Go <c>FurnitureRoom.Planning</c>: the room as the closers plan it: the plan input with the buildings that
    /// stand on it built, so a plan places only what is missing and never on top of what stands.
    /// </summary>
    public InteriorRoom Planning()
    {
        var built = new List<InteriorPiece>();
        foreach (var p in Pieces)
        {
            var b = new InteriorPiece { Slot = p.Thing, Def = p.Def, Size = p.Size, Rot = p.Rot, Rect = p.Rect };
            var (shape, ok) = Room.Shapes.Get(p.Def);
            if (ok && shape.Interaction is { } interaction)
            {
                b = b with { InteractionOffset = interaction };
            }
            built.Add(b);
        }
        return Room with { Built = built };
    }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Go <c>FurnitureRooms</c>: reads the rectangular census rooms that have a derived interior plan as
    /// furniture items: the doors are the observed doorways and the pieces every census building whose footprint
    /// lies on the floor.
    /// </summary>
    public static IReadOnlyList<FurnitureRoom> FurnitureRooms(RoomObservation rooms, CurrentConstruction census, IReadOnlyList<SiteCell> cells)
    {
        var doorways = new List<Cell>();
        foreach (var c in cells)
        {
            if (Positive(c.Doorway))
            {
                doorways.Add(c.Cell);
            }
        }
        var @out = new List<FurnitureRoom>();
        foreach (var room in rooms.Rooms)
        {
            if (!room.Role.TryGetValue(out var role) || room.Id.Length == 0)
            {
                continue;
            }
            var (planned, ok) = InteriorRoomFromCensus(room, role, doorways, rooms.Shapes);
            if (!ok)
            {
                continue;
            }
            var input = planned! with { Dining = rooms.Dining };
            var pieces = new List<FurniturePiece>();
            foreach (var b in census.Buildings)
            {
                if (b.ID.Length == 0 || b.Cells.Count == 0)
                {
                    continue;
                }
                var rect = CellsRectangle(b.Cells);
                if (!RectInside(input.Interior, rect))
                {
                    continue;
                }
                var rot = b.Building.Rotation();
                var size = rot is Rotation.East or Rotation.West ? new Cell(rect.Height, rect.Width) : new Cell(rect.Width, rect.Height);
                pieces.Add(new FurniturePiece { Thing = b.ID, Def = b.Building.Definition(), Size = size, Rot = rot, Rect = rect });
            }
            pieces = [.. pieces.OrderBy(p => p.Thing, StringComparer.Ordinal)];
            // The plan takes the room's standing definitions, so a bedroom is planned around the bed it has (a DoubleBed stays on plan).
            var standing = new List<string>(input.Standing);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in pieces)
            {
                if (seen.Add(p.Def))
                {
                    standing.Add(p.Def);
                }
            }
            standing.Sort(StringComparer.Ordinal);
            input = input with { Standing = standing };
            if (!RoomHasInterior(input, new InteriorPieceDef()))
            {
                continue;
            }
            @out.Add(new FurnitureRoom { Id = room.Id, Room = input, Pieces = pieces });
        }
        return [.. @out.OrderBy(r => r.Id, StringComparer.Ordinal)];
    }

    internal static bool RectInside(Rectangle outer, Rectangle inner) =>
        inner.X >= outer.X && inner.Z >= outer.Z && inner.X + inner.Width <= outer.X + outer.Width && inner.Z + inner.Height <= outer.Z + outer.Height;
}

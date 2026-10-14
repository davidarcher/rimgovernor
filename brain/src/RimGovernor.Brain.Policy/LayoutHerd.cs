namespace RimGovernor.Brain.Policy;

// Herd rooms. The pen is the herd's one room: a room node of the station graph (HerdRoomNodes) the tiler sites with the
// others, an outdoor ring of fence and gate round grazing ground with one PenMarker inside. The herds sleep in it, on
// the open ground: no animal asks for a roof, so there is no barn. A pen is matched to its herd by key: every pen of a
// herd, a top-up included, carries PlannedRoom.Herd, the herd's race; the misc unit's carry none.

public static partial class PolicyFunctions
{
    /// <summary>The plan's races with a ceiling above zero, in name order: the herd by race the station graph's pen nodes are sized from (#2996).</summary>
    public static IReadOnlyList<StationHerd> StationHerds(this HerdPlan p)
    {
        var output = new List<StationHerd>();
        foreach (var race in p.Policy.PopulationMax.Keys.OrderBy(r => r.Value, StringComparer.Ordinal))
        {
            var n = p.Policy.PopulationMax[race];
            if (n > 0)
            {
                output.Add(new StationHerd { Race = race, Animals = (int)n });
            }
        }
        return output;
    }

    /// <summary>
    /// A plan saved with its pens and yards as reservations: each outline becomes the room it was derived
    /// as, in plan order, under the next ids. kind is the reservation's wire name ("pen" or "yard"; a saved "barn" is
    /// dropped, for animals sleep in their pen), herd its unit key.
    /// </summary>
    public static LayoutPlan WithLegacySites(this LayoutPlan plan, IEnumerable<(string Kind, Region Area, string Herd)> legacy)
    {
        foreach (var (kind, area, herd) in legacy)
        {
            if (kind == "barn")
            {
                continue;
            }
            var outline = area.Bounds();
            var room = kind switch
            {
                "pen" => LayoutPlan.HerdRoom(outline, PlannedPen, herd),
                _ => LayoutPlan.YardRoom(outline),
            };
            var id = Math.Max(Math.Max(plan.NextRoomId, 1), plan.Rooms.Select(r => r.Id + 1).DefaultIfEmpty(1).Max());
            plan = plan with { Rooms = [.. plan.Rooms, room with { Id = id }], NextRoomId = id + 1 };
        }
        return plan;
    }

    /// <summary>Why the graph holds a pen: the herd's grazing outgrows its pens, or the plan already holds the pen.</summary>
    public const string ReasonHerdRoom = "herd_room_owed";

    /// <summary>
    /// The pens the plan holds and the herds still lack, as graph nodes. A standing pen is a node of the floor it has,
    /// keyed to its herd (RoomNode.Herd), so the tiler pins it; a herd short of the cells its pen wants (pens) gets one
    /// more pen node holding the shortfall. The ground it stands on is not constrained: a pen may stand on rock, at the
    /// price of the hay its herd is then hauled (PenNode.HandFed), a choice the forecast is to make among placements,
    /// not the embedder's.
    /// </summary>
    public static IReadOnlyList<RoomNode> HerdRoomNodes(LayoutPlan plan, IReadOnlyList<PenWant> pens)
    {
        var nodes = new List<RoomNode>();
        var herds = plan.RoomsOf(PlannedPen).Select(r => r.Herd).Concat(pens.Select(p => p.Race)).Distinct().Order(StringComparer.Ordinal);
        foreach (var herd in herds)
        {
            var n = 0;
            string Id() => $"{Wire.Name(PlannedPen)}/{herd}/{++n}";
            foreach (var pen in plan.HerdRooms(PlannedPen, herd))
            {
                var b = pen.Region.Bounds();
                nodes.Add(new RoomNode { Id = Id(), Role = PlannedPen, Herd = herd, Area = b.Width * b.Height, Reason = ReasonHerdRoom });
            }
            var want = pens.FirstOrDefault(p => p.Race == herd);
            var owed = want is null ? 0 : want.Cells - plan.PenCells(herd);
            if (owed > 0)
            {
                var (w, h) = PenSide(owed);
                nodes.Add(new RoomNode { Id = Id(), Role = PlannedPen, Herd = herd, Area = w * h, Reason = ReasonHerdRoom });
            }
        }
        return nodes;
    }

    /// <summary>The cells of the wall two outlines share between the corners both walls stand on, in order; empty unless they overlap in exactly one wall line.</summary>
    internal static List<Cell> SharedWallRun(Rectangle a, Rectangle b)
    {
        static List<Cell> Run(int a0, int a1, int b0, int b1, Func<int, Cell> at)
        {
            var output = new List<Cell>();
            for (var v = Math.Max(a0, b0) + 1; v < Math.Min(a1, b1) - 1; v++)
            {
                output.Add(at(v));
            }
            return output;
        }

        if (a.X + a.Width - 1 == b.X || b.X + b.Width - 1 == a.X)
        {
            var x = b.X;
            if (b.X + b.Width - 1 == a.X)
            {
                x = a.X;
            }
            return Run(a.Z, a.Z + a.Height, b.Z, b.Z + b.Height, z => new Cell(x, z));
        }
        if (a.Z + a.Height - 1 == b.Z || b.Z + b.Height - 1 == a.Z)
        {
            var z = b.Z;
            if (b.Z + b.Height - 1 == a.Z)
            {
                z = a.Z;
            }
            return Run(a.X, a.X + a.Width, b.X, b.X + b.Width, x => new Cell(x, z));
        }
        return [];
    }
}

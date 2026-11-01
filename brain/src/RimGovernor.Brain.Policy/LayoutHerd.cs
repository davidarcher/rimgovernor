namespace RimGovernor.Brain.Policy;

// Herd rooms. The pen is the herd's grazing room: a room node of the station graph (HerdRoomNodes) the tiler sites with
// the others, an outdoor ring of fence and gate round grazing ground with one PenMarker inside. Where the herd rests is
// compared, not ruled (HerdShelterRules): open pen ground, an animal sleeping spot on it, or a barn beside the pen and
// joined to it. A barn is a node only for a unit whose barn deviation the regroup applies (LayoutHerdBarnRules), and it stands beside its unit's
// pen, so the animals stay contained with their grazing. A herd room is matched to its herd by key: every pen and barn
// of a herd, a top-up included, carries PlannedRoom.Herd, the herd's race; the misc unit's carry none.

public static partial class LayoutHerdRules
{
    /// <summary>
    /// A plan saved with its pens and yards as reservations: each outline becomes the room it was derived
    /// as, in plan order, under the next ids. kind is the reservation's wire name ("barn", "pen" or "yard"), herd its unit key.
    /// </summary>
    public static LayoutPlan WithLegacySites(this LayoutPlan plan, IEnumerable<(string Kind, Region Area, string Herd)> legacy)
    {
        foreach (var (kind, area, herd) in legacy)
        {
            var outline = area.Bounds();
            var room = kind switch
            {
                "pen" => LayoutPlan.HerdRoom(outline, HerdRoomsRules.PlannedPen, herd),
                "barn" => LayoutPlan.HerdRoom(outline, PlannedRole.Barn, herd),
                _ => LayoutPlan.YardRoom(outline),
            };
            var id = Math.Max(Math.Max(plan.NextRoomId, 1), plan.Rooms.Select(r => r.Id + 1).DefaultIfEmpty(1).Max());
            plan = plan with { Rooms = [.. plan.Rooms, room with { Id = id }], NextRoomId = id + 1 };
        }
        return plan;
    }

    /// <summary>Why the graph holds a pen: the herd's grazing outgrows its pens, or the plan already holds the pen.</summary>
    public const string ReasonHerdRoom = "herd_room_owed";

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

partial record HerdPlan
{
    /// <summary>The plan's races with a ceiling above zero, in name order: the herd by race the station graph's pen nodes are sized from (#2996).</summary>
    public IReadOnlyList<StationHerd> StationHerds()
    {
        var output = new List<StationHerd>();
        foreach (var race in this.Policy.PopulationMax.Keys.OrderBy(r => r.Value, StringComparer.Ordinal))
        {
            var n = this.Policy.PopulationMax[race];
            if (n > 0)
            {
                output.Add(new StationHerd { Race = race, Animals = (int)n });
            }
        }
        return output;
    }
}

partial record LayoutPlan
{
    /// <summary>
    /// The pens and barns the plan holds and the herds still lack, as graph nodes. A standing pen or barn is a node of the
    /// floor it has, keyed to its herd (RoomNode.Herd), so the tiler pins it; a herd short of the cells its pen wants
    /// (pens) gets one more pen node holding the shortfall, and a herd whose barn deviation is applied (barns) and is short of
    /// the barn's cells gets one more barn node beside its first pen node. A herd with no pen node gets no barn: a barn
    /// apart from the pen would hold its animals away from their grazing. The ground a pen stands on is not constrained:
    /// a pen may stand on rock, at the price of the hay its herd is then hauled (PenNode.HandFed), a choice the forecast
    /// is to make among placements, not the embedder's.
    /// </summary>
    public static IReadOnlyList<RoomNode> HerdRoomNodes(LayoutPlan plan, IReadOnlyList<PenWant> pens, IReadOnlyList<BarnWant> barns)
    {
        var nodes = new List<RoomNode>();
        var herds = plan.RoomsOf(HerdRoomsRules.PlannedPen).Select(r => r.Herd).Concat(pens.Select(p => p.Race))
            .Concat(plan.RoomsOf(PlannedRole.Barn).Select(r => r.Herd)).Distinct().Order(StringComparer.Ordinal);
        foreach (var herd in herds)
        {
            var n = 0;
            string Id(PlannedRole role) => $"{Wire.Name(role)}/{herd}/{++n}";
            var penNodes = new List<RoomNode>();
            foreach (var pen in plan.HerdRooms(HerdRoomsRules.PlannedPen, herd))
            {
                var b = pen.Region.Bounds();
                penNodes.Add(new RoomNode { Id = Id(HerdRoomsRules.PlannedPen), Role = HerdRoomsRules.PlannedPen, Herd = herd, Area = b.Width * b.Height, Reason = LayoutHerdRules.ReasonHerdRoom });
            }
            var want = pens.FirstOrDefault(p => p.Race == herd);
            var owed = want is null ? 0 : want.Cells - plan.PenCells(herd);
            if (owed > 0)
            {
                var (w, h) = HerdPenRules.PenSide(owed);
                penNodes.Add(new RoomNode { Id = Id(HerdRoomsRules.PlannedPen), Role = HerdRoomsRules.PlannedPen, Herd = herd, Area = w * h, Reason = LayoutHerdRules.ReasonHerdRoom });
            }
            nodes.AddRange(penNodes);
            if (penNodes.Count == 0)
            {
                continue;
            }
            var standing = plan.HerdRooms(PlannedRole.Barn, herd);
            foreach (var barn in standing)
            {
                var b = barn.Region.Bounds();
                nodes.Add(new RoomNode { Id = Id(PlannedRole.Barn), Role = PlannedRole.Barn, Herd = herd, Area = b.Width * b.Height, Beside = penNodes[0].Id, Reason = HerdShelterRules.ReasonHerdShelter });
            }
            var barnWant = barns.FirstOrDefault(b => b.Race == herd);
            var barnOwed = barnWant is null ? 0 : barnWant.Cells - standing.Sum(r => r.Region.Bounds().Width * r.Region.Bounds().Height);
            if (barnOwed > 0)
            {
                nodes.Add(new RoomNode { Id = Id(PlannedRole.Barn), Role = PlannedRole.Barn, Herd = herd, Area = barnOwed, Beside = penNodes[0].Id, Reason = HerdShelterRules.ReasonHerdShelter });
            }
        }
        return nodes;
    }
}

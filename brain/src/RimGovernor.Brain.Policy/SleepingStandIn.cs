using System.Globalization;

namespace RimGovernor.Brain.Policy;

// The day-one floor (#3659): until a sleeping place and an indoor place stand for everyone (the foothold facts),
// colonists sleep on stand-in sleeping spots in the first selected sleeping room, or next to its ring. The first rooms
// are ordinary planned rooms the selection prices; the stand-ins only keep everyone off the bare ground meanwhile.

public static partial class ShelterRules
{
    /// <summary>The prefix of the stand-in Methods (StandInMethod).</summary>
    public const string StandInMethodPrefix = "stand-in-sleeping-";

    /// <summary>The planned roles a stand-in room is: the rooms that host sleeping.</summary>
    private static readonly PlannedRole[] SleepingRoles = [PlannedRole.Bedroom, PlannedRole.Barracks, PlannedRole.Suite];

    /// <summary>
    /// The stand-in sleeping spots owed this review: the foothold count less the standing sleeping places, while the
    /// foothold sleeping and indoor places are not both met; no Missing once they are. Count is the foothold count
    /// the shortfall is of.
    /// </summary>
    public static (long Count, int Missing) StandInsOwed(RoundsFacts f)
    {
        if (RoundsRules.Positive(RoundsRules.AllFacts(RoundsRules.FootholdShelter(f), RoundsRules.FootholdSleeping(f))))
        {
            return (0, 0);
        }
        var count = RoundsRules.FootholdCount(f);
        return (count, (int)Math.Max(0, count - f.BedCapacity));
    }

    /// <summary>
    /// The stand-in Method for <paramref name="missing"/> spots short of <paramref name="count"/> colonists: one per
    /// shortfall, so a spot lost while the floor is short is placed again.
    /// </summary>
    public static string StandInMethod(long count, int missing) =>
        string.Create(CultureInfo.InvariantCulture, $"{StandInMethodPrefix}{count}-{missing}");

    /// <summary>
    /// The room the stand-ins go in: the first sleeping room (Bedroom, Barracks or Suite) in plan order that is started
    /// or has sites the latest comparison selected, else the first planned sleeping room; null when the plan has none.
    /// </summary>
    public static PlannedRoom? StandInRoom(LayoutPlan plan, PlannedSelection record, Func<PlannedRoom, bool> started)
    {
        var sleeping = plan.Rooms.Where(r => SleepingRoles.Contains(r.Role)).ToList();
        return sleeping.FirstOrDefault(r => started(r) || record.Sites.Any(s => s.Room == r.Id)) ?? sleeping.FirstOrDefault();
    }

    /// <summary>
    /// The anchors a stand-in of North size <paramref name="size"/> may take at Rotation.South, each with its footprint
    /// (InteriorRules.OccupiedRect), in order: those wholly on the room's free interior (its floor less the footprints of
    /// the pieces the tiler placed in it), then those wholly outside it that touch its ring. A footprint never covers the
    /// ring (its door included) or another planned room's floor or ring. With no tiled pieces (LayoutPlan.Pieces) the
    /// interior is not known and only the outside anchors are offered. The footprints of different anchors may overlap:
    /// PickStandIns keeps the chosen ones apart.
    /// </summary>
    public static IReadOnlyList<(Cell Anchor, IReadOnlyList<Cell> Footprint)> StandInAnchors(LayoutPlan plan, PlannedRoom room, Cell size)
    {
        var ring = room.WallRing().Cells().ToHashSet();
        var others = new HashSet<Cell>();
        foreach (var r in plan.Rooms.Where(r => r.Id != room.Id))
        {
            others.UnionWith(r.Region.Cells());
            others.UnionWith(r.WallRing().Cells());
        }
        var output = new List<(Cell, IReadOnlyList<Cell>)>();
        IReadOnlyList<Cell> Footprint(Cell anchor) => PlannedRules.RectCells(InteriorRules.OccupiedRect(anchor, size, Rotation.South));
        if (plan.Pieces.TryGetValue(room.Id, out var pieces))
        {
            var taken = pieces.SelectMany(p => PlannedRules.RectCells(p.Rect)).ToHashSet();
            bool Free(Cell c) => room.Region.Contains(c) && !taken.Contains(c) && !others.Contains(c);
            foreach (var c in room.Region.Cells())
            {
                var footprint = Footprint(c);
                if (footprint.Count > 0 && footprint.All(Free))
                {
                    output.Add((c, footprint));
                }
            }
        }
        bool Outside(Cell c) => !ring.Contains(c) && !room.Region.Contains(c) && !others.Contains(c);
        bool Touches(Cell c) => ring.Contains(new Cell(c.X + 1, c.Z)) || ring.Contains(new Cell(c.X - 1, c.Z)) || ring.Contains(new Cell(c.X, c.Z + 1)) || ring.Contains(new Cell(c.X, c.Z - 1));
        var seen = new HashSet<Cell>();
        foreach (var c in ring.OrderBy(c => c.Z).ThenBy(c => c.X))
        {
            foreach (var n in new[] { new Cell(c.X + 1, c.Z), new Cell(c.X - 1, c.Z), new Cell(c.X, c.Z + 1), new Cell(c.X, c.Z - 1) })
            {
                if (!Outside(n) || !seen.Add(n))
                {
                    continue;
                }
                var footprint = Footprint(n);
                if (footprint.Count > 0 && footprint.All(Outside) && footprint.Any(Touches))
                {
                    output.Add((n, footprint));
                }
            }
        }
        return output;
    }

    /// <summary>
    /// The indices of up to <paramref name="missing"/> stand-ins to place, in candidate order: each <paramref name="placeable"/>,
    /// with its footprint off <paramref name="blocked"/> and off the footprints already picked, so no two spots claim a cell.
    /// </summary>
    public static IReadOnlyList<int> PickStandIns(IReadOnlyList<IReadOnlyList<Cell>> footprints, IReadOnlySet<Cell> blocked, Func<int, bool> placeable, int missing)
    {
        var claimed = new HashSet<Cell>(blocked);
        var output = new List<int>();
        for (var i = 0; i < footprints.Count && output.Count < missing; i++)
        {
            if (footprints[i].Any(claimed.Contains) || !placeable(i))
            {
                continue;
            }
            claimed.UnionWith(footprints[i]);
            output.Add(i);
        }
        return output;
    }
}

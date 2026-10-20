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
    /// foothold sleeping and indoor places are not both known met; no Missing once they are, or while a count is
    /// unknown. Count is the foothold count the shortfall is of.
    /// </summary>
    public static (long Count, int Missing) StandInsOwed(RoundsFacts f)
    {
        if (RoundsRules.Positive(RoundsRules.AllFacts(RoundsRules.FootholdShelter(f), RoundsRules.FootholdSleeping(f))))
        {
            return (0, 0);
        }
        if (!RoundsRules.FootholdCount(f).TryGetValue(out var count) || !f.BedCapacity.TryGetValue(out var beds))
        {
            return (0, 0);
        }
        return (count, (int)Math.Max(0, count - beds));
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
    /// The cells a stand-in may take, in order: the room's free interior (its floor less the footprints of the pieces the
    /// tiler placed in it), then the cells next to its ring outside it. With no tiled pieces (LayoutPlan.Pieces) the
    /// interior is not known and only the ring-adjacent cells are offered. A cell of another planned room's floor or ring
    /// is never offered.
    /// </summary>
    public static IReadOnlyList<Cell> StandInCells(LayoutPlan plan, PlannedRoom room)
    {
        var ring = room.WallRing().Cells().ToHashSet();
        var others = new HashSet<Cell>();
        foreach (var r in plan.Rooms.Where(r => r.Id != room.Id))
        {
            others.UnionWith(r.Region.Cells());
            others.UnionWith(r.WallRing().Cells());
        }
        var output = new List<Cell>();
        var seen = new HashSet<Cell>();
        if (plan.Pieces.TryGetValue(room.Id, out var pieces))
        {
            var taken = pieces.SelectMany(p => PlannedRules.RectCells(p.Rect)).ToHashSet();
            foreach (var c in room.Region.Cells())
            {
                if (!taken.Contains(c) && !others.Contains(c) && seen.Add(c))
                {
                    output.Add(c);
                }
            }
        }
        foreach (var c in ring.OrderBy(c => c.Z).ThenBy(c => c.X))
        {
            foreach (var n in new[] { new Cell(c.X + 1, c.Z), new Cell(c.X - 1, c.Z), new Cell(c.X, c.Z + 1), new Cell(c.X, c.Z - 1) })
            {
                if (!ring.Contains(n) && !room.Region.Contains(n) && !others.Contains(n) && seen.Add(n))
                {
                    output.Add(n);
                }
            }
        }
        return output;
    }
}

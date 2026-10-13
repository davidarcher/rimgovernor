namespace RimGovernor.Brain.Policy;

// Circulation is a station-less room node (RoomNode) that joins the rooms of a colony too large to reach
// through each other (#2911). Nothing here is a judgment constant:
//
//   - Need. A private room is one that costs something to cross and is not cold (RoomCrossing: sleepers,
//     cleanliness); nobody may walk through it. Every other room is a public one, a room pawns cross freely or
//     a cold room that is never crossed. Each private room needs a door onto a public room or the ground. When
//     the private rooms outnumber the public ones, no assignment of one distinct public room per private room
//     exists, so some hub carries two bedroom doors (each takes a floor cell and a clear cell the hub's area
//     did not count) and the sleepers walk through the kitchen and the freezer. At or below that count the
//     colony gets no corridor: the base is small enough that every room has a public neighbour of its own.
//   - Joined. The corridor is required beside every private room and every public room one of them has a flow
//     to (the table the sleepers eat at): the door of each opens on the corridor, not on a hub.
//   - Size. The corridor is CirculationWidth wide. Its length is what its two sides hold: each joined room
//     takes its block's long side (BlockShapes) of the side plus the wall cell that separates it from the next
//     (Frontage). A corridor is at most the longest strip the shell allows (ShellMaxWalls); a colony with more
//     rooms to join gets more corridors.
//
// The corridor is walked by everyone, so its crossing facts are empty; the tiler joins sealed rooms to it first
// (ThroughRank) and opens no flow door from a sleeper's room to a hub while it stands (SleepersUseCirculation).
public static partial class PolicyConstants
{
    /// <summary>Verdict of the circulation decision.</summary>
    public const string ClusterCirculate = "circulate";

    public const string ReasonClusterCirculation = "cluster_circulation";
    public const string ReasonClusterNoCirculation = "cluster_circulation_not_needed";
}

public static partial class PolicyFunctions
{
    /// <summary>Reports a room nobody may walk through that is not cold: it has crossing costs to price (RoomCrossing.Mechanisms).</summary>
    private static bool PrivateGroup(RoomGroup g)
    {
        var c = CrossingOf(g);
        return c.Crossable() && c.Mechanisms() > 0;
    }

    /// <summary>
    /// The length of corridor side a joined group's block takes with the wall cell that separates it from the
    /// next: its block's long side, for the embedder turns a block as the site asks and a corridor sized for
    /// the short side runs out of side before the last room is beside it.
    /// </summary>
    private static int Frontage(RoomGroup g, int depth)
    {
        var s = BlockShapes(g.Area + 1, depth)[0]; // the door onto the corridor adds a clear cell
        return Math.Max(s.W, s.H) + 1;
    }

    /// <summary>
    /// Appends the circulation corridors the node asks for to groups and links them to the rooms they join. It
    /// returns the groups unchanged with a ClusterKeep decision when the private rooms do not outnumber the
    /// public ones.
    /// </summary>
    internal static (IReadOnlyList<RoomGroup> Groups, ClusterDecision Decision) AddCirculation(IReadOnlyList<RoomGroup> groups, RoomNode node, int depth)
    {
        int privateCount = 0, publicCount = 0;
        foreach (var g in groups)
        {
            if (PrivateGroup(g))
            {
                privateCount++;
            }
            else
            {
                publicCount++;
            }
        }
        var d = new ClusterDecision { A = node.Id, Verdict = ClusterKeep, Reason = ReasonClusterNoCirculation };
        if (privateCount <= publicCount)
        {
            return (groups, d);
        }
        var joined = new List<int>();
        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            if (PrivateGroup(g))
            {
                joined.Add(i);
                continue;
            }
            foreach (var l in g.Links)
            {
                if (PrivateGroup(groups[l.To]))
                {
                    joined.Add(i);
                    break;
                }
            }
        }

        // A strip's longest run: the shell's wall ring is 2(w+h)+4 cells.
        var longest = ((ShellMaxWalls - 4) / 2) - node.Width;
        const int Sides = 2;
        var runs = new List<List<int>>(); // the joined groups on each corridor
        var cur = new List<int>();
        var used = 0;
        foreach (var i in joined)
        {
            var pitch = Frontage(groups[i], depth);
            if (cur.Count > 0 && (used + pitch + Sides - 1) / Sides > longest)
            {
                runs.Add(cur);
                cur = [];
                used = 0;
            }
            cur.Add(i);
            used += pitch;
        }
        runs.Add(cur);
        var output = groups.ToList();
        foreach (var run in runs)
        {
            var sum = 0;
            foreach (var i in run)
            {
                sum += Frontage(groups[i], depth);
            }
            var length = (sum + Sides - 1) / Sides;
            var area = length * node.Width;
            var id = output.Count;
            var links = new List<GroupLink>();
            foreach (var i in run)
            {
                links.Add(new GroupLink { To = i, Required = true });

                // The door onto the corridor keeps a threshold clear, as every required link does (AddRequiredDoors).
                output[i] = output[i] with
                {
                    Links = [.. output[i].Links, new GroupLink { To = id, Required = true }],
                    Clearance = output[i].Clearance + 1,
                    Area = output[i].Area + 1,
                };
            }
            output.Add(new RoomGroup { Id = id, Footprint = 0, Clearance = area, Area = area, Planned = node.Role, Width = node.Width, Links = links });
        }
        foreach (var i in joined)
        {
            output[i] = output[i] with { Links = [.. output[i].Links.OrderBy(l => l.To)] };
        }
        d = d with { Verdict = ClusterCirculate, Reason = ReasonClusterCirculation };
        return (output, d);
    }
}

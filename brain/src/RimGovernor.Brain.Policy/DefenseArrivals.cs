namespace RimGovernor.Brain.Policy;

/// <summary>
/// Identifies one ground raid, rather than a pawn or a turret burst. Edge is the observed boundary crossing:
/// the first sampled position of the raid inside the census. It is matched to the sector whose edge cell lies
/// nearest, within DefenseArrivalSnap cells.
/// </summary>
public sealed record DefenseArrival(string Id, Cell Edge, Tick Tick);

/// <summary>
/// A connected run on one side of the census boundary. Home reachability and Route use only observed passable
/// cells. A bounded census establishes local approaches, not all possible routes across the map.
/// </summary>
public sealed record DefenseSector
{
    public EquatableArray<Cell> Edge { get; init; }

    public int RecentRaids { get; init; }

    /// <summary>Runs from the nearest opening in the sector to Entry, with the proposed funnel walls treated as impassable.</summary>
    public EquatableArray<Cell> Route { get; init; }
}

/// <summary>Stewardship demand on one cell (the bridge's <c>DefenseCover</c> is the clearance identity of a thing, which this is not). Sector indexes the ranked Sectors; a Hold keeps the cell visible but forbids treating it as clearance work.</summary>
public sealed record DefenseCoverDemand(Cell Cell, int Sector, string Hold);

public sealed record DefenseApproaches
{
    public EquatableArray<DefenseSector> Sectors { get; init; }

    public EquatableArray<DefenseCoverDemand> Cover { get; init; }

    public string Hold { get; init; } = "";
}

public static class DefenseArrivalRoutes
{
    internal const long DefenseArrivalWindow = 3 * GameTime.TicksPerDay;
    internal const int DefenseRouteTail = 6;

    /// <summary>The cover fill a cell must exceed to be cover demand on an approach: any fill at all.</summary>
    internal const double CoverFillThreshold = 0.0;

    /// <summary>Bounds how far an arrival's crossing may lie from a sector's edge cell: the native trail is sampled every 60 ticks, a few cells of raider movement.</summary>
    internal const int DefenseArrivalSnap = 8;

    /// <summary>Cheap furniture on the arrival sector's approach, outside the killbox: looters break off to grab it, splitting the raid before it reaches the corridor.</summary>
    public static readonly DefenseTierName TierBait = new("bait");

    /// <summary>The bait pieces bound: a few split a group, more only cost wood.</summary>
    internal const int DefenseBaitCount = 2;

    /// <summary>The least route distance from Entry: a piece nearer would pull looters into the killbox rather than off the line.</summary>
    internal const int DefenseBaitStandoff = 10;

    /// <summary>Keeps pieces apart along the route, so each draws its own looters.</summary>
    internal const int DefenseBaitSpacing = 3;

    internal static void ValidateDefenseArrivals(DefenseRequest r)
    {
        if (r.Tick.Value < 0)
        {
            throw new DefenseException("invalid defense arrival census");
        }
        var seen = new Dictionary<string, DefenseArrival>(StringComparer.Ordinal);
        foreach (var a in r.Arrivals)
        {
            if (a.Id == "" || a.Tick.Value < 0 || a.Tick > r.Tick || a.Edge.X < 0 || a.Edge.Z < 0 || a.Edge.X >= r.Bounds.Width || a.Edge.Z >= r.Bounds.Height)
            {
                throw new DefenseException("invalid defense arrival");
            }
            if (seen.TryGetValue(a.Id, out var old) && old != a)
            {
                throw new DefenseException("conflicting defense arrival");
            }
            seen[a.Id] = a;
        }
    }


    internal static bool DefenseCellLess(Cell a, Cell b) => a.Z != b.Z ? a.Z < b.Z : a.X < b.X;

    internal static List<Cell> DefensePath(Dictionary<Cell, Cell> prev, Cell from)
    {
        var path = new List<Cell>();
        while (true)
        {
            if (!prev.TryGetValue(from, out var next))
            {
                return [];
            }
            path.Add(from);
            if (next == from)
            {
                return path;
            }
            from = next;
        }
    }

    /// <summary>
    /// Recomputes the approaches of an accepted layout against a fresh census: the layout's tiers, lanes and
    /// firing cells stay protected, and the request's arrivals and any cell with cover fill inside the defenders' range decide the cover demand.
    /// </summary>
    public static DefenseApproaches DefenseApproachesFor(DefenseRequest r, DefenseLayout l) => DefenseSite.Create(r).ComputeApproaches(l);
}

internal sealed partial class DefenseSite
{
    /// <summary>One bounded flood, shared by all sectors instead of one search per edge cell. Unknowns and projected walls cannot connect components.</summary>
    internal (Dictionary<Cell, Cell> Prev, Dictionary<Cell, int> Distance) Paths(Cell start, HashSet<Cell>? closed)
    {
        var prev = new Dictionary<Cell, Cell>();
        var distance = new Dictionary<Cell, int>();
        bool Closed(Cell c) => closed is not null && closed.Contains(c);
        if (!Passable(start) || Closed(start))
        {
            return (prev, distance);
        }
        prev[start] = start;
        distance[start] = 0;
        var queue = new List<Cell> { start };
        for (var i = 0; i < queue.Count; i++)
        {
            var c = queue[i];
            foreach (var d in DefenseLayout.Directions)
            {
                var n = DefenseLayout.AddCell(c, d);
                if (prev.ContainsKey(n) || Closed(n) || !Passable(n))
                {
                    continue;
                }
                prev[n] = c;
                distance[n] = distance[c] + 1;
                queue.Add(n);
            }
        }
        return (prev, distance);
    }

    internal int BorderSide(Cell c)
    {
        var r = R.Region;
        if (c.Z == r.Z)
        {
            return 0;
        }
        if (c.X == r.X + r.Width - 1)
        {
            return 1;
        }
        if (c.Z == r.Z + r.Height - 1)
        {
            return 2;
        }
        return 3;
    }

    /// <summary>Floods from origin: Home while it is passable, else the layout's Entry.</summary>
    internal List<DefenseSector> ComputeSectors(Cell origin)
    {
        var (_, distance) = Paths(origin, null);
        var edge = new HashSet<Cell>();
        var ordered = new List<Cell>();
        foreach (var c in distance.Keys)
        {
            if (OnBorder(c) && Cells[c].EdgeReachable)
            {
                edge.Add(c);
                ordered.Add(c);
            }
        }
        ordered = ordered.OrderBy(c => c.Z).ThenBy(c => c.X).ToList();
        var sectors = new List<(List<Cell> Edge, int PathLength, int RecentRaids)>();
        var seen = new HashSet<Cell>();
        foreach (var first in ordered)
        {
            if (!seen.Add(first))
            {
                continue;
            }
            var sectorEdge = new List<Cell> { first };
            var pathLength = distance[first];
            for (var i = 0; i < sectorEdge.Count; i++)
            {
                var c = sectorEdge[i];
                if (distance[c] < pathLength)
                {
                    pathLength = distance[c];
                }
                foreach (var d in DefenseLayout.Directions)
                {
                    var n = DefenseLayout.AddCell(c, d);
                    if (edge.Contains(n) && !seen.Contains(n) && BorderSide(n) == BorderSide(first))
                    {
                        seen.Add(n);
                        sectorEdge.Add(n);
                    }
                }
            }
            sectorEdge = sectorEdge.OrderBy(c => c.Z).ThenBy(c => c.X).ToList();
            sectors.Add((sectorEdge, pathLength, 0));
        }
        var byCell = new Dictionary<Cell, int>();
        for (var i = 0; i < sectors.Count; i++)
        {
            foreach (var c in sectors[i].Edge)
            {
                byCell[c] = i;
            }
        }
        var counted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in R.Arrivals)
        {
            if (counted.Contains(a.Id) || R.Tick.Value - a.Tick.Value > DefenseArrivalRoutes.DefenseArrivalWindow)
            {
                continue;
            }
            counted.Add(a.Id);
            var ok = byCell.TryGetValue(a.Edge, out var i);
            if (!ok)
            {
                // The crossing was sampled a few cells inside the border: the nearest sector edge cell within
                // the snap radius, the lowest-ordered on a tie.
                var nearest = (long)((DefenseArrivalRoutes.DefenseArrivalSnap * DefenseArrivalRoutes.DefenseArrivalSnap) + 1);
                foreach (var c in ordered)
                {
                    var d = PlannedRules.SquaredDistance(c, a.Edge);
                    if (d < nearest)
                    {
                        i = byCell[c];
                        ok = true;
                        nearest = d;
                    }
                }
            }
            if (!ok)
            {
                // A crossing that maps to no sector counts for none.
                continue;
            }
            var sector = sectors[i];
            sector.RecentRaids++;
            sectors[i] = sector;
        }
        return sectors
            .OrderByDescending(x => x.RecentRaids)
            .ThenBy(x => x.PathLength)
            .ThenBy(x => x.Edge[0].Z)
            .ThenBy(x => x.Edge[0].X)
            .Select(x => new DefenseSector
            {
                Edge = EquatableArray.From(x.Edge),
                RecentRaids = x.RecentRaids,
            })
            .ToList();
    }

    internal DefenseApproaches ComputeApproaches(DefenseLayout l)
    {
        var origin = R.Home;
        if (!Passable(origin))
        {
            origin = l.Entry;
        }
        var sectorList = ComputeSectors(origin);
        var closed = new HashSet<Cell>();
        var protectedCells = new HashSet<Cell>(Protect);
        foreach (var t in l.Tiers)
        {
            foreach (var c in t.Reserved)
            {
                protectedCells.Add(c);
            }
            foreach (var b in t.Buildings)
            {
                protectedCells.Add(b.Cell);
                // Raiders cannot open a player door: the service lane and the back-wall doorway are no route for them.
                if (b.Definition == R.Definitions.Wall || b.Definition == R.Definitions.Door)
                {
                    closed.Add(b.Cell);
                }
            }
        }
        foreach (var c in l.TrapLane)
        {
            protectedCells.Add(c);
            // Existing rock can be the corridor wall without a placement in any tier. Removing it would open the
            // flank of the accepted layout.
            foreach (var d in DefenseLayout.Directions)
            {
                var n = DefenseLayout.AddCell(c, d);
                if (Blocking(n))
                {
                    protectedCells.Add(n);
                }
            }
        }
        foreach (var f in l.Firing)
        {
            protectedCells.Add(f.Cell);
            protectedCells.Add(f.Cover);
            protectedCells.Add(f.Retreat);
        }
        foreach (var (c, row) in Cells)
        {
            if (row.Door && row.PlayerOwned)
            {
                closed.Add(c);
            }
        }
        var (prev, distance) = Paths(l.Entry, closed);
        for (var i = 0; i < sectorList.Count; i++)
        {
            var sector = sectorList[i];
            var found = false;
            var best = default(Cell);
            foreach (var c in sector.Edge)
            {
                if (distance.TryGetValue(c, out var d) && (!found || d < distance[best]))
                {
                    best = c;
                    found = true;
                }
            }
            if (found)
            {
                sector = sector with { Route = EquatableArray.From(DefenseArrivalRoutes.DefensePath(prev, best)) };
            }
            sectorList[i] = sector;
        }
        var output = new DefenseApproaches { Sectors = EquatableArray.From(sectorList) };
        if (l.Firing.Count == 0)
        {
            return output with { Hold = "no_firing_line" };
        }
        var rangeLimit = R.MinRange;
        var dir = DefenseTurretsPolicy.DirectionOf(l.Toward);
        var cover = new List<DefenseCoverDemand>();
        foreach (var (c, row) in Cells)
        {
            if (protectedCells.Contains(c) || row.CoverFill <= DefenseArrivalRoutes.CoverFillThreshold)
            {
                continue;
            }
            var inRange = false;
            foreach (var f in l.Firing)
            {
                if (PlannedRules.SquaredDistance(c, f.Cell) <= rangeLimit * rangeLimit)
                {
                    inRange = true;
                    break;
                }
            }
            if (!inRange)
            {
                continue;
            }
            var forward = ((c.X - l.Entry.X) * dir.X) + ((c.Z - l.Entry.Z) * dir.Z) <= 0;
            var sectorIndex = -1;
            var nearest = long.MaxValue;
            for (var i = 0; i < sectorList.Count; i++)
            {
                var route = sectorList[i].Route;
                var start = Math.Max(0, route.Count - DefenseArrivalRoutes.DefenseRouteTail);
                for (var j = start; j < route.Count; j++)
                {
                    var dist = PlannedRules.SquaredDistance(c, route[j]);
                    if ((forward || dist <= 2) && dist < nearest)
                    {
                        sectorIndex = i;
                        nearest = dist;
                    }
                }
            }
            if (sectorIndex < 0)
            {
                continue;
            }
            var hold = "";
            if (row.NaturalRock && (c.X == 0 || c.Z == 0 || c.X == R.Bounds.Width - 1 || c.Z == R.Bounds.Height - 1))
            {
                hold = "map_edge_rock";
            }
            if (row.NaturalRock && hold == "")
            {
                var exposed = false;
                foreach (var delta in DefenseLayout.Directions)
                {
                    exposed = exposed || Passable(DefenseLayout.AddCell(c, delta));
                }
                if (!exposed)
                {
                    hold = "mountain_interior";
                }
            }
            if (row.NaturalRock && hold == "")
            {
                // A rock face is terrain: mining one cell of a mass exposes the next, and the mass may be the wall
                // the layout leans on. Only a lone rock (no natural rock beside it) is orderable.
                foreach (var delta in DefenseLayout.Directions)
                {
                    var n = DefenseLayout.AddCell(c, delta);
                    if (Cells.TryGetValue(n, out var nrow) && nrow.NaturalRock && !Passable(n))
                    {
                        hold = "rock_face";
                        break;
                    }
                }
            }
            cover.Add(new DefenseCoverDemand(c, sectorIndex, hold));
        }
        // Nearest the entry first: the cover a raider would take at the line is cleared before the cover farther up the approach.
        var sortedCover = cover
            .OrderBy(x => x.Sector)
            .ThenBy(x => PlannedRules.SquaredDistance(x.Cell, l.Entry))
            .ThenBy(x => x.Cell.Z)
            .ThenBy(x => x.Cell.X)
            .ToList();
        return output with { Cover = EquatableArray.From(sortedCover) };
    }

    /// <summary>
    /// Places Definitions.Bait beside the top-ranked sector's route, only once a raid has arrived there: without
    /// an observed arrival the sector is a guess. Pieces stand beside the route, never on it, walking outward
    /// from the standoff.
    /// </summary>
    internal DefenseTier BaitTier(DefenseLayout l)
    {
        var tier = new DefenseTier { Name = DefenseArrivalRoutes.TierBait };
        var def = R.Definitions.Bait;
        if (def == "" || l.Approaches.Sectors.Count == 0)
        {
            return tier;
        }
        var sector = l.Approaches.Sectors[0];
        if (sector.RecentRaids == 0 || sector.Route.Count == 0)
        {
            return tier;
        }
        var taken = new HashSet<Cell>(sector.Route);
        foreach (var c in l.TrapLane)
        {
            taken.Add(c);
        }
        foreach (var t in l.Tiers)
        {
            foreach (var c in t.Reserved)
            {
                taken.Add(c);
            }
            foreach (var b in t.Buildings)
            {
                taken.Add(b.Cell);
            }
        }
        foreach (var f in l.Firing)
        {
            taken.Add(f.Cell);
            taken.Add(f.Cover);
            taken.Add(f.Retreat);
        }
        var buildings = new List<Building>();
        var reserved = new List<Cell>();
        // Route runs from the opening to Entry, so index len-1 is Entry.
        var last = -DefenseArrivalRoutes.DefenseBaitSpacing;
        for (var i = sector.Route.Count - 1 - DefenseArrivalRoutes.DefenseBaitStandoff; i >= 0 && buildings.Count < DefenseArrivalRoutes.DefenseBaitCount; i--)
        {
            var distance = sector.Route.Count - 1 - i;
            if (distance - last < DefenseArrivalRoutes.DefenseBaitSpacing)
            {
                continue;
            }
            Cell at = sector.Route[i], next = sector.Route[i + 1];
            var p = DefenseLayout.Perpendicular(new Cell(next.X - at.X, next.Z - at.Z));
            foreach (var c in new[] { DefenseLayout.AddCell(at, p), DefenseLayout.AddCell(at, DefenseLayout.Scale(p, -1)) })
            {
                if (taken.Contains(c) || !Free(c))
                {
                    continue;
                }
                var b = DefenseIEDs.TryBuilding(def, c, R.Definitions.BaitStuff);
                if (b is null)
                {
                    continue;
                }
                buildings.Add(b);
                reserved.Add(c);
                taken.Add(c);
                last = distance;
                break;
            }
        }
        return tier with
        {
            Buildings = EquatableArray.From(buildings),
            Reserved = EquatableArray.From(reserved),
        };
    }
}

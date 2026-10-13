using System.Globalization;

namespace RimGovernor.Brain.Policy;

// Infestation prevention. Insects dig out of open ground under overhead mountain that is dark: a small unused
// pocket of such ground beside the base is walled solid, and every planned room gets lamps lighting its whole
// floor above the darkness threshold.
//
// Each pocket is a NodeKind.PocketWall site node produced after the ring node (#2925): a fill of open
// overhead-mountain cells near the base the perimeter tier walls solid. It has no room, only the cells.
public static partial class PolicyConstants
{
    /// <summary>A small dark pocket touching the ring or within PocketReach of it, that nothing else uses.</summary>
    public const string ReasonPocketBesideBase = "pocket_beside_base";

    /// <summary>The largest pocket walled solid; a bigger open cave is a room or the infestation response's business.</summary>
    internal const int PocketMaxCells = 20;

    /// <summary>How far outside the perimeter ring a pocket still counts as beside the base. A judgment constant: the game offers no signal for it.</summary>
    internal const int PocketReach = 6;

    /// <summary>Names of the pocket-wall sections under the perimeter's prefix, so the perimeter recut builds and removes them with the wall.</summary>
    public const string TierPocketPrefix = TierPerimeterPrefix + "pocket-";

    /// <summary>Names of the room-lamp sections under the perimeter's prefix.</summary>
    public const string TierLightPrefix = TierPerimeterPrefix + "light-";
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Replaces plan's pocket-wall nodes with one per small pocket: a 4-connected patch of at most
    /// PocketMaxCells walkable, unbuilt cells under thick roof, touching the perimeter ring or within
    /// PocketReach of it, that no room, hallway or other site node or reservation uses. A plan without a
    /// perimeter holds none.
    /// </summary>
    public static LayoutPlan PlanMountainPockets(LayoutPlan plan, MapSurvey s)
    {
        plan = plan.WithoutSites(NodeKind.PocketWall);
        if (!plan.Ring().Ok)
        {
            return plan;
        }
        var used = new HashSet<Cell>();
        foreach (var r in plan.Reserved())
        {
            foreach (var c in r.Area.Cells())
            {
                used.Add(c);
            }
        }
        foreach (var r in plan.Rooms)
        {
            foreach (var c in RectCells(RoomWalls(r)))
            {
                used.Add(c);
            }
        }
        var (wi, _) = PlanInterior(plan, PocketReach);
        var near = wi.Near(PocketReach);
        var open = new HashSet<Cell>();
        foreach (var c in s.Cells)
        {
            if (c.ThickRoof && c.Walkable && !c.Rock && !c.Built)
            {
                open.Add(c.Cell);
            }
        }
        var seen = new HashSet<Cell>();
        var starts = LessSorting.SortedBy(open, CellLess);
        var sites = plan.Sites.ToList();
        foreach (var start in starts)
        {
            if (seen.Contains(start))
            {
                continue;
            }
            var patch = new List<Cell> { start };
            seen.Add(start);
            for (var i = 0; i < patch.Count; i++)
            {
                var c = patch[i];
                foreach (var n in new[] { new Cell(c.X + 1, c.Z), new Cell(c.X - 1, c.Z), new Cell(c.X, c.Z + 1), new Cell(c.X, c.Z - 1) })
                {
                    if (open.Contains(n) && seen.Add(n))
                    {
                        patch.Add(n);
                    }
                }
            }
            if (patch.Count > PocketMaxCells)
            {
                continue;
            }
            bool beside = false, free = true;
            foreach (var c in patch)
            {
                beside = beside || near(c) >= 0;
                free = free && !used.Contains(c);
            }
            if (!beside || !free)
            {
                continue;
            }
            var live = sites.Count(x => x.Kind == NodeKind.PocketWall && !x.Retired);
            var id = string.Create(CultureInfo.InvariantCulture, $"{Wire.Name(NodeKind.PocketWall)}-{live:D2}");
            sites.Add(NewSite(id, NodeKind.PocketWall, RegionOfRuns(CellRuns(patch)), ReasonPocketBesideBase));
        }
        return plan with { Sites = sites };
    }

    /// <summary>One section per pocket-wall node, each cell a wall with its stuff left for admission to fill.</summary>
    public static IReadOnlyList<PerimeterSection> PocketSections(LayoutPlan plan, string wall)
    {
        var output = new List<PerimeterSection>();
        foreach (var r in plan.LiveSites(NodeKind.PocketWall))
        {
            var buildings = r.Footprint.Cells().Select(c => new Building(wall, c, Rotation.North, string.Empty)).ToList();
            output.Add(new PerimeterSection
            {
                Name = new DefenseTierName(string.Create(CultureInfo.InvariantCulture, $"{TierPocketPrefix}{output.Count:D2}")),
                Buildings = buildings,
            });
        }
        return output;
    }

    /// <summary>
    /// The lamp cells lighting every built room of plan (all but reserve rooms): a grid over each interior whose
    /// lamps sit no farther than reach (the standing lamp's glowReach from the catalog) from any floor cell. An
    /// unknown reach places none. Rooms keep plan order.
    /// </summary>
    public static IReadOnlyList<Cell> BaseRoomLamps(LayoutPlan plan, double reach)
    {
        if (!FinitePositive(reach))
        {
            return [];
        }
        var output = new List<Cell>();
        foreach (var r in plan.Rooms)
        {
            if (r.Role == PlannedRole.Reserve)
            {
                continue;
            }
            var interior = r.Region.Bounds();
            // A lamp covers a square of side reach*sqrt2 around it.
            var span = (int)Math.Floor(reach * Math.Sqrt(2));
            if (!r.Region.IsRect())
            {
                output.AddRange(ShapedRoomLamps(r.Region, span, reach));
                continue;
            }
            int nx = (interior.Width + span - 1) / span, nz = (interior.Height + span - 1) / span;
            for (var iz = 0; iz < nz; iz++)
            {
                for (var ix = 0; ix < nx; ix++)
                {
                    output.Add(new Cell(interior.X + ((((2 * ix) + 1) * interior.Width) / (2 * nx)), interior.Z + ((((2 * iz) + 1) * interior.Height) / (2 * nz))));
                }
            }
        }
        return output;
    }

    /// <summary>One section holding every lamp placed by BaseRoomLamps, in plan order.</summary>
    public static IReadOnlyList<PerimeterSection> LightSections(LayoutPlan plan, string lamp, double reach)
    {
        var cells = BaseRoomLamps(plan, reach);
        if (cells.Count == 0)
        {
            return [];
        }
        return
        [
            new PerimeterSection
            {
                Name = new DefenseTierName(TierLightPrefix + "00"),
                Buildings = cells.Select(c => new Building(lamp, c, Rotation.North, string.Empty)).ToList(),
            },
        ];
    }

    /// <summary>
    /// Lights a non-rectangular region (an L, a ring): one lamp on the floor cell nearest the middle of each
    /// span-square of the bounds that holds floor, then a lamp on every floor cell still farther than reach from
    /// all lamps, row-major. Lamps never stand off the floor.
    /// </summary>
    private static List<Cell> ShapedRoomLamps(Region region, int span, double reach)
    {
        var interior = region.Bounds();
        var cells = region.Cells().OrderBy(c => c.Z).ThenBy(c => c.X).ToList();
        int nx = (interior.Width + span - 1) / span, nz = (interior.Height + span - 1) / span;
        var output = new List<Cell>();
        for (var iz = 0; iz < nz; iz++)
        {
            for (var ix = 0; ix < nx; ix++)
            {
                var mid = new Cell(interior.X + ((((2 * ix) + 1) * interior.Width) / (2 * nx)), interior.Z + ((((2 * iz) + 1) * interior.Height) / (2 * nz)));
                int x0 = interior.X + (ix * interior.Width / nx), z0 = interior.Z + (iz * interior.Height / nz);
                int x1 = interior.X + ((ix + 1) * interior.Width / nx), z1 = interior.Z + ((iz + 1) * interior.Height / nz);
                var best = default(Cell);
                var found = false;
                foreach (var c in cells)
                {
                    if (c.X < x0 || c.X >= x1 || c.Z < z0 || c.Z >= z1)
                    {
                        continue;
                    }
                    if (!found || DistanceSquared(c, mid) < DistanceSquared(best, mid))
                    {
                        (best, found) = (c, true);
                    }
                }
                if (found)
                {
                    output.Add(best);
                }
            }
        }
        foreach (var c in cells)
        {
            if (!output.Any(l => Distance(l, c) <= reach))
            {
                output.Add(c);
            }
        }
        return output;
    }
}

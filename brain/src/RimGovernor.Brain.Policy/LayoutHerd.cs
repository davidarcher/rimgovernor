namespace RimGovernor.Brain.Policy;

// Herd sites. The barn is a reservation sized by the herd plan, grouped in units: the misc unit holds every
// animal that is no herd's, and each herd (HerdPlan.Herds) gets a unit of its own, sized from its policy
// ceiling. A unit is a barn (roofed, an animal sleeping spot per animal, the bedroom of every animal of the
// unit, bonded ones included). The hospital, not a room of the herd's, treats a sick animal (herd_ward). A
// reservation never changes or moves: a herd its unit cannot hold gets another barn beside the same unit.
//
// A unit is matched to its herd by key: every reservation of a herd's unit, a top-up included, carries
// LayoutReservation.Herd, the herd's race; the misc unit's carry none. A plan without keys is not matched: its
// units all join the misc unit.

/// <summary>One herd's unit size: the race (the key its reservations carry, LayoutReservation.Herd) and its policy ceiling.</summary>
public sealed record HerdCeiling(string Race, int Animals);

/// <summary>One unit's reservations: its barns, in plan order.</summary>
internal sealed class HerdUnit
{
    public List<Rectangle> Barns { get; } = [];

    /// <summary>The race the unit's reservations are keyed to; empty for the misc unit and for a plan saved before keys.</summary>
    public string Herd { get; init; } = string.Empty;

    /// <summary>The middle of the unit's first barn, the core when it has none.</summary>
    public (int X, int Z) Centre(UtilityGrid u) =>
        Barns.Count > 0 ? (Barns[0].X + (Barns[0].Width / 2), Barns[0].Z + (Barns[0].Height / 2)) : (u.Cx, u.Cz);
}

public static partial class PolicyConstants
{
    /// <summary>The smallest herd the misc unit is sized for, before the plan has a target.</summary>
    internal const int PenAnimalsFloor = HerdUnplannedFloor;
}

public static partial class PolicyFunctions
{
    /// <summary>The misc unit's herd: the plan's ceiling summed over the races that are no herd of their own, at least PenAnimalsFloor.</summary>
    public static int PenAnimals(this HerdPlan p)
    {
        long n = 0;
        foreach (var (race, ceiling) in p.Policy.PopulationMax)
        {
            if (!p.Herds.Contains(race))
            {
                n += ceiling;
            }
        }
        return Math.Max((int)n, PenAnimalsFloor);
    }

    /// <summary>The herds' units, one per herd race in name order: each gets a barn of its own, sized from its ceiling.</summary>
    public static IReadOnlyList<HerdCeiling> HerdUnits(this HerdPlan p) =>
        p.Herds.Order(StringComparer.Ordinal).Select(race => new HerdCeiling(race, (int)p.Policy.PopulationMax.GetValueOrDefault(race))).ToList();

    /// <summary>The plan's races with a ceiling above zero, in name order: the herd by race the station graph's pen nodes are sized from (#2996).</summary>
    public static IReadOnlyList<StationHerd> StationHerds(this HerdPlan p)
    {
        var output = new List<StationHerd>();
        foreach (var race in p.Policy.PopulationMax.Keys.Order(StringComparer.Ordinal))
        {
            var n = p.Policy.PopulationMax[race];
            if (n > 0)
            {
                output.Add(new StationHerd { Race = race, Animals = (int)n });
            }
        }
        return output;
    }

    /// <summary>The outline of a room whose interior is w by h.</summary>
    internal static (int W, int H) WalledSide(int w, int h) => (w + 2, h + 2);

    /// <summary>
    /// Tops plan up with the herd units: the misc unit for animals animals and one unit per herds ceiling, each
    /// with the barn beds the unit's existing rooms cannot hold. Nothing placed moves; sites that do not fit are
    /// left out. Zero animals plans no misc unit. plan holds its core zones.
    /// </summary>
    public static LayoutPlan PlanHerdSites(LayoutPlan plan, int animals, params IReadOnlyList<HerdCeiling> herds)
    {
        if ((animals <= 0 && herds.Count == 0) || plan.Rooms.Count == 0)
        {
            return plan;
        }
        var reservations = plan.Reservations.ToList();
        plan = plan with { Reservations = reservations };
        PlanHerdSitesIn(new UtilityGrid(plan), ref plan, animals, herds);
        return plan;
    }

    /// <summary>The plan's barn reservations grouped into units by their herd key, in order of first appearance.</summary>
    internal static List<HerdUnit> HerdUnitsOf(this LayoutPlan p)
    {
        var units = new List<HerdUnit>();
        var at = new Dictionary<string, int>();
        foreach (var r in p.Reservations)
        {
            if (r.Kind != ReservationKind.Barn)
            {
                continue;
            }
            if (!at.TryGetValue(r.Herd, out var k))
            {
                k = units.Count;
                at[r.Herd] = k;
                units.Add(new HerdUnit { Herd = r.Herd });
            }
            units[k].Barns.Add(r.Area.Bounds());
        }
        return units;
    }

    /// <summary>The 1x1 beds the rooms of role hold on the areas.</summary>
    internal static int Capacity(this LayoutPlan p, IEnumerable<Rectangle> areas, PlannedRole role)
    {
        var n = 0;
        foreach (var a in areas)
        {
            n += HerdRoomBeds(p.HerdRoom(a, role), new PieceShapes(), new InteriorPieceDef { Def = "bed", Size = new Cell(1, 1) });
        }
        return n;
    }

    /// <summary>
    /// The plan's herd units matched to the primary units (the misc unit, then one per herd): a unit goes to the
    /// herd its reservations are keyed to, so an unkeyed one, or one of a herd no longer planned, joins the misc
    /// unit.
    /// </summary>
    internal static List<HerdUnit> HousedUnits(this LayoutPlan p, IReadOnlyList<HerdCeiling> herds)
    {
        var output = new List<HerdUnit> { new() };
        foreach (var h in herds)
        {
            output.Add(new HerdUnit { Herd = h.Race });
        }
        foreach (var g in p.HerdUnitsOf())
        {
            var k = herds.ToList().FindIndex(h => h.Race == g.Herd);
            if (g.Herd == string.Empty)
            {
                k = -1;
            }
            output[k + 1].Barns.AddRange(g.Barns); // -1, misc or a gone herd's unit
        }
        return output;
    }

    private static void PlanHerdSitesIn(UtilityGrid u, ref LayoutPlan plan, int misc, IReadOnlyList<HerdCeiling> herds)
    {
        var units = plan.HousedUnits(herds);
        for (var i = 0; i < units.Count; i++)
        {
            var (race, animals) = (string.Empty, misc);
            if (i > 0)
            {
                (race, animals) = (herds[i - 1].Race, herds[i - 1].Animals);
            }
            if (animals <= 0)
            {
                continue;
            }
            // A herd's unit prefers a site near the misc unit's.
            var (cx, cz) = (u.Cx, u.Cz);
            if (i > 0)
            {
                (cx, cz) = units[0].Centre(u);
            }
            PlanUnit(u, ref plan, units[i], race, animals, cx, cz, i == 0);
        }
    }

    /// <summary>
    /// Tops one unit up for animals: a barn holding a sleeping spot per animal. A herd the unit's barns outgrow
    /// gets another barn beside the unit, else on free ground near it; none moves. Only a unit with nothing
    /// placed yet takes a site near cx, cz. Every reservation it adds carries herd, the key of the unit's race
    /// (empty for misc). The shortfall is then met, so a repeat top-up is a no-op.
    /// </summary>
    private static void PlanUnit(UtilityGrid u, ref LayoutPlan plan, HerdUnit unit, string herd, int animals, int cx, int cz, bool misc)
    {
        if (unit.Barns.Count > 0)
        {
            cx = unit.Barns[0].X + (unit.Barns[0].Width / 2);
            cz = unit.Barns[0].Z + (unit.Barns[0].Height / 2);
        }
        var shortfall = animals - plan.Capacity(unit.Barns, PlannedRole.Barn);
        if (shortfall <= 0)
        {
            return;
        }
        var beds = shortfall;
        if (misc && unit.Barns.Count == 0)
        {
            beds = Math.Max(beds, HerdBarnMinBeds);
        }
        var (hw, hh) = HerdSide(beds);
        var (w, h) = WalledSide(hw, hh);
        var site = default(Rectangle);
        var ok = false;
        foreach (var anchor in unit.Barns)
        {
            (site, ok) = u.BesideReservation(plan, anchor, ReservationKind.Barn, w, h, null);
            if (ok)
            {
                break;
            }
        }
        if (!ok)
        {
            (site, ok) = u.Site(w, h, cx, cz);
        }
        if (ok)
        {
            u.Reserve(ref plan, new LayoutReservation { Kind = ReservationKind.Barn, Area = RectRegion(site), Herd = herd });
        }
    }

    /// <summary>The cell of the wall two reservations' outlines share where a door joins them: the middle of the run between the corners both walls stand on. False unless the outlines overlap in exactly one wall line.</summary>
    internal static (Cell Cell, bool Ok) SharedWallLink(Rectangle a, Rectangle b)
    {
        var run = SharedWallRun(a, b);
        if (run.Count == 0)
        {
            return (default, false);
        }
        return (run[(run.Count - 1) / 2], true);
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

    /// <summary>
    /// Reports that adding cand to the plan leaves every herd room's door where it was and no two of the rooms'
    /// doors, links and pens' animal flaps on one cell.
    /// </summary>
    internal static bool DoorsHold(this LayoutPlan p, LayoutReservation cand)
    {
        var next = p with { Reservations = [.. p.Reservations, cand] };
        var seen = new HashSet<Cell>();
        foreach (var r in next.Reservations)
        {
            var (role, ok) = HerdRole(r.Kind);
            if (!ok)
            {
                continue;
            }
            var room = next.HerdRoom(r.Area.Bounds(), role);
            if (!r.Equals(cand) && p.HerdRoom(r.Area.Bounds(), role).Door != room.Door)
            {
                return false;
            }
            foreach (var c in next.DoorsOf(room))
            {
                if (!seen.Add(c))
                {
                    return false;
                }
            }
        }
        foreach (var c in next.Flaps())
        {
            if (!seen.Add(c))
            {
                return false;
            }
        }
        return true;
    }
}

internal sealed partial class UtilityGrid
{
    /// <summary>
    /// The nearest w x h outline of kind sharing one wall with anchor (no wall material is wasted on a second
    /// parallel wall): every other cell must be free, no room's door moves or lands on the shared link (the link
    /// takes a door, or a pen's animal flap), and accept, when set, takes the outline.
    /// </summary>
    public (Rectangle Site, bool Ok) BesideReservation(LayoutPlan plan, Rectangle anchor, ReservationKind kind, int w, int h, Func<Rectangle, bool>? accept)
    {
        int cx = anchor.X + (anchor.Width / 2), cz = anchor.Z + (anchor.Height / 2);
        var best = default(Rectangle);
        var bestCost = 0.0;
        var found = false;
        foreach (var (sdx, sdz) in new (int, int)[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            int x0 = anchor.X, z0 = anchor.Z;
            if (sdx > 0)
            {
                x0 = anchor.X + anchor.Width - 1;
            }
            else if (sdx < 0)
            {
                x0 = anchor.X - w + 1;
            }
            else if (sdz > 0)
            {
                z0 = anchor.Z + anchor.Height - 1;
            }
            else
            {
                z0 = anchor.Z - h + 1;
            }
            for (var off = -h - anchor.Height; off <= h + anchor.Height + w + anchor.Width; off++)
            {
                var r = new Rectangle(x0, z0 + off, w, h);
                if (sdz != 0)
                {
                    r = new Rectangle(x0 + off, z0, w, h);
                }
                double dx = r.X + (w / 2) - cx, dz = r.Z + (h / 2) - cz;
                var cost = Math.Sqrt((dx * dx) + (dz * dz));
                if (found && cost >= bestCost)
                {
                    continue;
                }
                if (!PolicyFunctions.SharedWallLink(r, anchor).Ok
                    || (accept is not null && !accept(r))
                    || !Inset(r)
                    || !FreeBesideWall(r, anchor)
                    || !plan.DoorsHold(new LayoutReservation { Kind = kind, Area = RectRegion(r) }))
                {
                    continue;
                }
                (best, bestCost, found) = (r, cost, true);
            }
        }
        return (best, found);
    }

    /// <summary>Reports every cell of r free, bar the cells of anchor's own outline: the shared wall.</summary>
    public bool FreeBesideWall(Rectangle r, Rectangle anchor)
    {
        for (var z = r.Z; z < r.Z + r.Height; z++)
        {
            for (var x = r.X; x < r.X + r.Width; x++)
            {
                if (x >= anchor.X && x < anchor.X + anchor.Width && z >= anchor.Z && z < anchor.Z + anchor.Height)
                {
                    // Only the anchor's wall may be shared, never its interior.
                    if (x > anchor.X && x < anchor.X + anchor.Width - 1 && z > anchor.Z && z < anchor.Z + anchor.Height - 1)
                    {
                        return false;
                    }
                    continue;
                }
                if (!Free(new Rectangle(x, z, 1, 1), false))
                {
                    return false;
                }
            }
        }
        return true;
    }
}

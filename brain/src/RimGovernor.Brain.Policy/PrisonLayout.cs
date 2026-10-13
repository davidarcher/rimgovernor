using System.Globalization;
using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Policy.PolicyConstants;

namespace RimGovernor.Brain.Policy;

// Prison layout: mini-turrets stand just outside the prison doors, and no weapon stockpile stands near a
// prison, so an escapee finds neither an open exit nor a weapon. Layout keeps the armory and every prison
// that far apart (weaponClearance).

public static partial class PolicyConstants
{
    /// <summary>Names the prison turret sections under the perimeter's prefix, each with its conduit run, as the pumps do.</summary>
    public const string TierPrisonTurretPrefix = TierPerimeterPrefix + "prison-";

    /// <summary>The Chebyshev distance a weapon stockpile keeps from every prison cell (walls and door included).</summary>
    internal const int PrisonWeaponClearance = 6;
}

public static partial class PolicyFunctions
{
    /// <summary>The cell just outside a room's door and the direction out; false when the door does not border the interior.</summary>
    internal static (Cell Front, Cell Out, bool Ok) PrisonDoorOutside(PlannedRoom r)
    {
        foreach (var d in Directions)
        {
            if (Contains(r.Region.Bounds(), AddCell(r.Door, d)))
            {
                var outward = new Cell(-d.X, -d.Z);
                return (AddCell(r.Door, outward), outward, true);
            }
        }
        return (default, default, false);
    }

    /// <summary>
    /// The mini-turret cells outside the plan's prison doors: one each side of the cell in front of a door,
    /// never on that cell (the walkway) nor on any room's footprint or reservation.
    /// </summary>
    public static IReadOnlyList<Cell> PrisonTurretSites(LayoutPlan plan)
    {
        var blocked = new HashSet<Cell>();
        foreach (var r in plan.Rooms)
        {
            foreach (var c in RectCells(Pad(r.Region.Bounds(), 1)))
            {
                blocked.Add(c);
            }
        }
        foreach (var r in plan.Reservations)
        {
            foreach (var c in r.Area.Cells())
            {
                blocked.Add(c);
            }
        }
        var output = new List<Cell>();
        foreach (var r in plan.Rooms)
        {
            if (r.Role != PlannedPrison)
            {
                continue;
            }
            var (front, dir, ok) = PrisonDoorOutside(r);
            if (!ok)
            {
                continue;
            }
            blocked.Add(front);
            var p = Perpendicular(dir);
            foreach (var c in new[] { AddCell(front, p), AddCell(front, Scale(p, -1)) })
            {
                if (blocked.Add(c))
                {
                    output.Add(c);
                }
            }
        }
        return output;
    }

    /// <summary>
    /// One section per prison turret site: the turret and the conduit run that brings a transmitter within
    /// connector reach, chained over the ring's interior clear of the killbox and the turret sites. Nothing
    /// without a transmitter: a dark turret guards nothing.
    /// </summary>
    public static IReadOnlyList<PerimeterSection> PerimeterPrisonTurrets(LayoutPlan plan, string turret, string stuff, string conduit, IReadOnlyList<Cell> transmitters)
    {
        var sites = PrisonTurretSites(plan);
        if (sites.Count == 0 || transmitters.Count == 0)
        {
            return [];
        }
        var killbox = default(Rectangle);
        var (k, found) = plan.Killbox();
        if (found)
        {
            killbox = k!.Footprint.Bounds();
        }
        var wi = PlanInterior(plan, 0);
        var allowed = new HashSet<Cell>();
        var carry = new HashSet<Cell>();
        if (wi is not null)
        {
            foreach (var c in wi.Cells())
            {
                if (!Contains(killbox, c))
                {
                    allowed.Add(c);
                }
            }
        }
        foreach (var c in sites)
        {
            allowed.Remove(c);
        }
        foreach (var c in transmitters)
        {
            allowed.Add(c);
            carry.Add(c);
        }
        var output = new List<PerimeterSection>();
        foreach (var site in sites)
        {
            var chain = new DefenseSite(new DefenseRequest()).ConduitChain(site, carry, allowed);
            if (chain is null)
            {
                continue;
            }
            var s = new PerimeterSection
            {
                Name = new DefenseTierName(string.Create(CultureInfo.InvariantCulture, $"{TierPrisonTurretPrefix}{output.Count:D2}")),
            };
            s.Buildings.Add(new Building(turret, site, Rotation.North, stuff));
            foreach (var c in chain)
            {
                s.Buildings.Add(new Building(conduit, c, Rotation.North, string.Empty));
                carry.Add(c);
            }
            output.Add(s);
        }
        return output;
    }

    /// <summary>Every cell of the plan's prisons, walls included.</summary>
    public static IReadOnlyList<Cell> PrisonCells(LayoutPlan plan)
    {
        var output = new List<Cell>();
        foreach (var r in plan.Rooms)
        {
            if (r.Role == PlannedPrison)
            {
                output.AddRange(RectCells(Pad(r.Region.Bounds(), 1)));
            }
        }
        return output;
    }

    /// <summary>Reports whether any cell lies within the weapon clearance of a prison cell.</summary>
    internal static bool NearPrison(IReadOnlyList<Cell> cells, IReadOnlyList<Cell> prisons)
    {
        foreach (var c in cells)
        {
            foreach (var p in prisons)
            {
                if (Chebyshev(c, p) <= PrisonWeaponClearance)
                {
                    return true;
                }
            }
        }
        return false;
    }
}

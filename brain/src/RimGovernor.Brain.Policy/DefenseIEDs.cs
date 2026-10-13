namespace RimGovernor.Brain.Policy;

/// <summary>
/// One IED definition the research and content gates allow, with its native explosive radius. Incendiary IEDs
/// also keep that radius from every player building.
/// </summary>
public sealed record DefenseIED(string Definition, double Radius, bool Incendiary);

public static partial class PolicyFunctions
{
    /// <summary>The IED traps on the approach ahead of the killbox corridor: the trap lane extended outward from Entry toward the map edge.</summary>
    public static readonly DefenseTierName TierIEDs = new("ieds");

    /// <summary>How far past Entry the approach is searched.</summary>
    internal const int DefenseIEDReach = 16;

    /// <summary>Keeps IEDs two cells apart along the approach: PlaceWorker_NeverAdjacentTrap refuses a trap beside another.</summary>
    internal const int DefenseIEDSpacing = 2;

    internal const int IedBaseBudget = 2;

    /// <summary>
    /// The number of approach IEDs the observed raid points demand: the base two, plus one for every
    /// TurretStepPoints. Unknown, NaN or infinite threat keeps the base.
    /// </summary>
    public static int IEDBudget(Fact<double> raidPoints)
    {
        if (!raidPoints.TryGetValue(out var points) || double.IsNaN(points) || double.IsInfinity(points) || points < TurretStepPoints)
        {
            return IedBaseBudget;
        }
        return IedBaseBudget + (int)(points / TurretStepPoints);
    }

    /// <summary>
    /// Reports every keepOff cell strictly farther than radius from <paramref name="at"/>: the game's explosion
    /// reaches cells within the radius. A non-positive or non-finite radius is not a known blast and fails.
    /// </summary>
    internal static bool OutsideBlast(Cell at, double radius, IEnumerable<Cell> keepOff)
    {
        if (!(radius > 0) || double.IsInfinity(radius))
        {
            return false;
        }
        foreach (var c in keepOff)
        {
            if (SquaredDistance(at, c) <= radius * radius)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>A validated building, or null when the domain refuses it.</summary>
    internal static Building? TryBuilding(string definition, Cell cell, string stuff)
    {
        try
        {
            return new Building(definition, cell, Rotation.North, stuff);
        }
        catch (DomainException)
        {
            return null;
        }
    }
}

internal sealed partial class DefenseSite
{
    /// <summary>
    /// Walks the approach outward from Entry along the trap lane and places an IED on each free cell, every
    /// DefenseIEDSpacing cells, whose blast cannot reach a door, the safe lane, a colonist route or flammable
    /// storage; an incendiary IED also stays clear of every player building, observed or planned. The first
    /// listed IED that fits a cell takes it. An unknown storage census, no IED definition or no qualifying
    /// cell leaves the tier empty.
    /// </summary>
    internal DefenseTier IedTier(DefenseLayout l, CorridorCosts costs)
    {
        var tier = new DefenseTier { Name = TierIEDs };
        if (R.IEDs.Count == 0 || !R.FlammableStorage.TryGetValue(out var storage))
        {
            return tier;
        }
        var keepOff = new List<Cell>(storage);
        keepOff.AddRange(R.Entrances);
        foreach (var (c, row) in Cells)
        {
            if (Positive(row.Door))
            {
                keepOff.Add(c);
            }
        }
        foreach (var start in new[] { R.Home }.Concat(R.Entrances))
        {
            var (route, _) = ColonistRoute(costs, start);
            if (route is not null)
            {
                keepOff.AddRange(route);
            }
        }
        var buildings = new List<Cell>(keepOff);
        foreach (var (c, row) in Cells)
        {
            if (Positive(row.PlayerOwned))
            {
                buildings.Add(c);
            }
        }
        var taken = new HashSet<Cell>();
        foreach (var t in l.Tiers)
        {
            foreach (var c in t.Reserved)
            {
                taken.Add(c);
            }
            foreach (var b in t.Buildings)
            {
                taken.Add(b.Cell);
                buildings.Add(b.Cell);
            }
        }
        foreach (var f in l.Firing)
        {
            taken.Add(f.Cell);
            taken.Add(f.Cover);
            taken.Add(f.Retreat);
        }
        var back = Scale(DirectionOf(l.Toward), -1);
        var placed = new List<Building>();
        var reserved = new List<Cell>();
        for (var k = DefenseIEDSpacing; k <= DefenseIEDReach; k += DefenseIEDSpacing)
        {
            var c = AddCell(l.Entry, Scale(back, k));
            if (!InRegion(c) || taken.Contains(c) || !Free(c) || !Passable(c))
            {
                continue;
            }
            foreach (var ied in R.IEDs)
            {
                var clear = ied.Incendiary ? buildings : keepOff;
                if (!OutsideBlast(c, ied.Radius, clear))
                {
                    continue;
                }
                var b = TryBuilding(ied.Definition, c, "");
                if (b is null)
                {
                    continue;
                }
                placed.Add(b);
                reserved.Add(c);
                break;
            }
        }
        var gated = "";
        if (placed.Count > R.IEDMax)
        {
            var keep = Math.Max(R.IEDMax, 0);
            placed = placed.Take(keep).ToList();
            reserved = reserved.Take(keep).ToList();
            gated = GateIEDThreat;
        }
        while (placed.Count > 0 && !StockCovers(R.UnitCosts, R.IEDStock, placed))
        {
            placed.RemoveAt(placed.Count - 1);
            reserved.RemoveAt(reserved.Count - 1);
            gated = GateIEDStock;
        }
        return tier with
        {
            Buildings = EquatableArray.From(placed),
            Reserved = EquatableArray.From(reserved),
            Gated = gated,
            Costs = TierCosts(R.UnitCosts, placed),
        };
    }
}

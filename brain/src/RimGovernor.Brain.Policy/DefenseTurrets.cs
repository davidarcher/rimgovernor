namespace RimGovernor.Brain.Policy;

/// <summary>
/// The powered turret tier's input. Every gate is an observation: Available is the native planning definition's
/// availability, DrawW its base draw, SpareW the chosen power network's spare watts, Transmitters the conduit
/// cells that carry that network, and Stock the colony's stock census. An unknown gate places no turret.
/// Definition empty asks for no turret tier.
/// </summary>
public sealed record DefenseTurretRequest
{
    public string Definition { get; init; } = "";

    public string Stuff { get; init; } = "";

    public string Conduit { get; init; } = "";

    public Fact<bool> Available { get; init; }

    public Fact<double> DrawW { get; init; }

    public Fact<double> SpareW { get; init; }

    public EquatableArray<Cell> Transmitters { get; init; }

    public Fact<IReadOnlyDictionary<Resource, long>> Stock { get; init; }

    /// <summary>Bounds the turrets placed; zero or negative places none.</summary>
    public int Max { get; init; }

    /// <summary>The definition's North footprint; zero is 1x1.</summary>
    public Bounds Size { get; init; }
}

/// <summary>
/// The part of a layout the turret tier places against: the corridor entry, the kill zone a turret must see
/// some cell of, the corridor direction, the shooters' cells and every cell the other tiers occupy or reserve.
/// </summary>
public sealed record DefenseGeometry
{
    public Cell Entry { get; init; }

    public EquatableArray<Cell> Approach { get; init; }

    public Rotation Toward { get; init; }

    public EquatableArray<Cell> Firing { get; init; }

    /// <summary>The corridor's cells; no turret or conduit stands on one.</summary>
    public EquatableArray<Cell> Lanes { get; init; }

    public EquatableArray<Cell> Reserved { get; init; }

    /// <summary>The turret tier's cells; the mortar tier keeps off and clear of them.</summary>
    public EquatableArray<Cell> Turrets { get; init; }

    /// <summary>The funnel tier's walls and doorway: no turret stands on one, but a conduit may run beneath it.</summary>
    public EquatableArray<Cell> Walls { get; init; }
}

/// <summary>A turret cell with line of sight to the lane.</summary>
public sealed record TurretPosition(Cell Cell);

/// <summary>One tier turret the census found standing, with the cells it occupies.</summary>
public sealed record StandingTurret(Building Building, EquatableArray<Cell> Cells);

public static partial class PolicyFunctions
{
    // The turret ladder: the rung is the armory tier (AssessArmory), Machining allowing the autocannon and
    // Fabrication the uranium slug turret. Each rung's own research and content still gate it through its
    // planning definition's availability. The foam turret is not a rung.
    public const string TurretMini = "Turret_MiniTurret";
    public const string TurretAutocannon = "Turret_Autocannon";
    public const string TurretSniper = "Turret_Sniper";

    /// <summary>The rungs bottom first.</summary>
    public static readonly IReadOnlyList<string> TurretLadder = [TurretMini, TurretAutocannon, TurretSniper];

    private static ArmoryTier TurretRungTier(string definition) => definition switch
    {
        TurretAutocannon => ArmoryTier.Machining,
        TurretSniper => ArmoryTier.Fabrication,
        _ => ArmoryTier.Unknown,
    };

    public static readonly DefenseTierName TierTurrets = new("turrets");

    /// <summary>The minimum Chebyshev distance between two turrets and between a turret and a shooter: a destroyed turret explodes, and a chain of them takes the firing line with it.</summary>
    internal const int TurretSpacing = 3;

    /// <summary>The native PowerConnectionMaker search rect: a consumer connects to the nearest transmitter within six cells.</summary>
    internal const int ConduitReach = 6;

    // Gated reasons: the gate that stopped a tier short of the positions the layout found, journaled by the caller.
    public const string GateTurretUnavailable = "turret_unavailable";
    public const string GateTurretThreat = "turret_threat_budget";
    public const string GateTurretPower = "turret_power";
    public const string GateTurretStock = "turret_stock";
    public const string GateTurretRoute = "turret_no_conduit_route";
    public const string GateMortarUnavailable = "mortar_unavailable";
    public const string GateMortarThreat = "mortar_threat_budget";
    public const string GateMortarStock = "mortar_stock";
    public const string GateIEDThreat = "ied_threat_budget";
    public const string GateIEDStock = "ied_stock";

    /// <summary>The ladder definitions the armory tier allows, highest first; the mini turret is always allowed.</summary>
    public static IReadOnlyList<string> TurretRungs(ArmoryTier tier)
    {
        var output = new List<string>();
        for (var i = TurretLadder.Count - 1; i >= 0; i--)
        {
            if (TurretRungTier(TurretLadder[i]) <= tier)
            {
                output.Add(TurretLadder[i]);
            }
        }
        return output;
    }

    /// <summary>A definition's rung on the ladder, -1 off it.</summary>
    public static int TurretRank(string definition)
    {
        for (var i = 0; i < TurretLadder.Count; i++)
        {
            if (TurretLadder[i] == definition)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>The cells a turret of the given North size occupies anchored at a cell, rotation North (a zero size is 1x1).</summary>
    public static IReadOnlyList<Cell> TurretFootprint(Cell anchor, Bounds size)
    {
        if (size.Width < 1 || size.Height < 1)
        {
            size = new Bounds { Width = 1, Height = 1 };
        }
        var rect = OccupiedRect(anchor, new Cell(size.Width, size.Height), Rotation.North);
        var output = new List<Cell>();
        for (var x = rect.X; x < rect.X + rect.Width; x++)
        {
            for (var z = rect.Z; z < rect.Z + rect.Height; z++)
            {
                output.Add(new Cell(x, z));
            }
        }
        return output;
    }

    /// <summary>The Chebyshev distance between the nearest cells of two footprints: edge to edge.</summary>
    internal static int FootprintGap(IEnumerable<Cell> a, IReadOnlyCollection<Cell> b)
    {
        var gap = int.MaxValue;
        foreach (var x in a)
        {
            foreach (var y in b)
            {
                gap = Math.Min(gap, Chebyshev(x, y));
            }
        }
        return gap;
    }

    internal static Cell DirectionOf(Rotation r) => r switch
    {
        Rotation.North => Directions[0],
        Rotation.East => Directions[1],
        Rotation.South => Directions[2],
        _ => Directions[3],
    };

    /// <summary>The turret tier's view of the layout.</summary>
    public static DefenseGeometry Geometry(this DefenseLayout l)
    {
        var reserved = new List<Cell>();
        var walls = new List<Cell>();
        foreach (var t in l.Tiers)
        {
            if (t.Name == TierTurrets)
            {
                continue;
            }
            reserved.AddRange(t.Reserved);
            foreach (var b in t.Buildings)
            {
                reserved.Add(b.Cell);
                if (t.Name == TierFunnel)
                {
                    walls.Add(b.Cell);
                }
            }
        }
        return new DefenseGeometry
        {
            Entry = l.Entry,
            Approach = l.TrapLane,
            Toward = l.Toward,
            Lanes = l.TrapLane,
            Firing = EquatableArray.From(l.Firing.Select(f => f.Cell)),
            Reserved = EquatableArray.From(reserved),
            Walls = EquatableArray.From(walls),
        };
    }

    /// <summary>
    /// Proposes the powered turret tier for a layout: turrets beside the firing positions on the shooters' row or
    /// the row behind it, spaced against chain explosions, off the lanes and every reserved cell, each with a
    /// line of sight to some cell of the lane, and a conduit chain from each turret to the network unless a
    /// transmitter already lies within native connector reach. Candidates lists every position that sees the
    /// lane; the tier holds the ones the gates allow.
    /// </summary>
    public static (DefenseTier Tier, IReadOnlyList<TurretPosition> Candidates) DefenseTurrets(DefenseRequest r, DefenseGeometry g)
    {
        var (tier, candidates) = DefenseSite.Create(r).Turrets(g);
        return (tier, candidates);
    }

    /// <summary>Reports whether the observed gates (available definition, spare watts, stock) allow at least one turret, before any site or line of sight is read.</summary>
    public static bool TurretGatesOpen(this DefenseRequest r) => TurretGate(r, 1) > 0;

    /// <summary>How many of the verified positions the power and stock gates allow.</summary>
    internal static int TurretGate(DefenseRequest r, int verified) => TurretLimit(r, verified).Count;

    /// <summary>TurretGate with the gate that cut the count below the verified positions ("" when nothing did): the threat demand (Max), spare watts, then stock.</summary>
    internal static (int Count, string Gate) TurretLimit(DefenseRequest r, int verified)
    {
        var q = r.Turret;
        if (!q.Available.TryGetValue(out var available) || !available
            || !q.DrawW.TryGetValue(out var draw) || !q.SpareW.TryGetValue(out var spare) || draw < 0)
        {
            return (0, GateTurretUnavailable);
        }
        if (q.Max <= 0)
        {
            return (0, GateTurretThreat);
        }
        var n = q.Max;
        var gate = GateTurretThreat;
        if (n >= verified)
        {
            n = verified;
            gate = "";
        }
        if (draw > 0)
        {
            while (n > 0 && n * draw > spare)
            {
                n--;
                gate = GateTurretPower;
            }
        }
        while (n > 0)
        {
            var turrets = new List<Building>();
            for (var i = 0; i < n; i++)
            {
                var b = TryBuilding(q.Definition, default, q.Stuff);
                if (b is null)
                {
                    return (0, GateTurretUnavailable);
                }
                turrets.Add(b);
            }
            if (Affordable(r, turrets))
            {
                break;
            }
            n--;
            gate = GateTurretStock;
        }
        return (n, gate);
    }

    /// <summary>Reports whether the stock census covers the buildings' summed unit costs; unknown costs or stock never afford anything.</summary>
    internal static bool Affordable(DefenseRequest r, IEnumerable<Building> buildings)
    {
        if (!TierCosts(r.UnitCosts, buildings).TryGetValue(out var costs) || !r.Turret.Stock.TryGetValue(out var stock))
        {
            return false;
        }
        foreach (var a in costs)
        {
            if (stock.GetValueOrDefault(a.Resource) < a.Count)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Drops the last turret and the conduits routed for it.</summary>
    internal static List<Building> TrimLastTurret(List<Building> buildings, string turret)
    {
        var last = -1;
        for (var i = 0; i < buildings.Count; i++)
        {
            if (buildings[i].Definition == turret)
            {
                last = i;
            }
        }
        return last < 0 ? [] : buildings.GetRange(0, last);
    }

    /// <summary>
    /// Picks the one standing turret to replace in place with the request's higher rung: the first below the
    /// rung whose new footprint, anchored on the same cell, covers only its own cells or free ground off the
    /// lanes and reservations, keeps turret spacing from the shooters and every other turret, and whose gates
    /// are open. Null replaces nothing.
    /// </summary>
    public static (Building Old, Building Replacement)? TurretReplacement(DefenseRequest r, DefenseGeometry g, IReadOnlyList<StandingTurret> standing)
    {
        var q = r.Turret;
        var rank = TurretRank(q.Definition);
        if (rank <= 0 || TurretGate(r, 1) == 0)
        {
            return null;
        }
        var s = DefenseSite.Create(r);
        var taken = new HashSet<Cell>(g.Lanes);
        taken.UnionWith(g.Reserved);
        for (var i = 0; i < standing.Count; i++)
        {
            var t = standing[i];
            var have = TurretRank(t.Building.Definition);
            if (have < 0 || have >= rank)
            {
                continue;
            }
            var own = new HashSet<Cell> { t.Building.Cell };
            own.UnionWith(t.Cells);
            var fp = TurretFootprint(t.Building.Cell, q.Size);
            var fits = FootprintGap(fp, g.Firing) >= TurretSpacing;
            foreach (var c in fp)
            {
                fits = fits && (own.Contains(c) || (s.Free(c) && !taken.Contains(c)));
            }
            for (var j = 0; j < standing.Count; j++)
            {
                if (j != i && fits)
                {
                    var other = standing[j];
                    var cells = new List<Cell> { other.Building.Cell };
                    cells.AddRange(other.Cells);
                    fits = FootprintGap(fp, cells) >= TurretSpacing;
                }
            }
            if (!fits)
            {
                continue;
            }
            var b = new Building(q.Definition, t.Building.Cell, Rotation.North, q.Stuff);
            return (t.Building, b);
        }
        return null;
    }
}

internal sealed partial class DefenseSite
{
    internal (DefenseTier Tier, IReadOnlyList<TurretPosition> Candidates) Turrets(DefenseGeometry g)
    {
        var tier = new DefenseTier { Name = TierTurrets, Costs = Known(EquatableArray.From(new List<Amount>())) };
        var q = R.Turret;
        if (q.Definition == "")
        {
            return (tier, []);
        }
        if (q.Conduit == "")
        {
            throw new DefenseException("invalid turret request");
        }
        if (g.Firing.Count == 0 || g.Approach.Count == 0 || !InRegion(g.Entry))
        {
            return (tier, []);
        }
        var d = DirectionOf(g.Toward);
        var p = Perpendicular(d);
        var taken = new HashSet<Cell>(g.Lanes);
        taken.UnionWith(g.Reserved);
        // Firing cells share the shooters' row; their offsets along p from the first one bound the span the turrets flank.
        var origin = g.Firing[0];
        int low = 0, high = 0;
        foreach (var f in g.Firing)
        {
            var o = ((f.X - origin.X) * p.X) + ((f.Z - origin.Z) * p.Z);
            low = Math.Min(low, o);
            high = Math.Max(high, o);
        }
        var candidates = new List<TurretPosition>();
        var chosen = new List<IReadOnlyList<Cell>>();
        // Every occupied cell must be free and off the lanes and reservations, and the footprint keeps turret
        // spacing edge to edge from the shooters and every other turret.
        bool Clear(Cell c)
        {
            var fp = TurretFootprint(c, q.Size);
            foreach (var x in fp)
            {
                if (!Free(x) || taken.Contains(x))
                {
                    return false;
                }
            }
            if (FootprintGap(fp, g.Firing) < TurretSpacing)
            {
                return false;
            }
            foreach (var t in chosen)
            {
                if (FootprintGap(fp, t.ToList()) < TurretSpacing)
                {
                    return false;
                }
            }
            return true;
        }

        // Slots flank the firing span, kept TurretSpacing apart by Clear, on the shooters' row and the row
        // behind it: a mini turret takes the front row first, a heavier rung (the autocannon's minimum range)
        // the back row. A row's slots end at the kill zone's side wall.
        var rows = new[] { origin, AddCell(origin, d) };
        if (TurretRank(q.Definition) > 0)
        {
            (rows[0], rows[1]) = (rows[1], rows[0]);
        }
        var positions = new List<Cell>();
        foreach (var row in rows)
        {
            foreach (var (edge, dir) in new[] { (high, 1), (low, -1) })
            {
                for (var step = 1; step <= DefenseMaxWidth; step++)
                {
                    var c = AddCell(row, Scale(p, edge + (dir * step)));
                    if (taken.Contains(c) || Blocking(c))
                    {
                        break;
                    }
                    positions.Add(c);
                }
            }
        }
        foreach (var c in positions)
        {
            if (!Clear(c))
            {
                continue;
            }
            // Kept when it has a known line of sight to some cell of the lane.
            var sees = false;
            foreach (var a in g.Approach)
            {
                if (LineOfSight(c, a).TryGetValue(out var los) && los)
                {
                    sees = true;
                    break;
                }
            }
            if (!sees)
            {
                continue;
            }
            candidates.Add(new TurretPosition(c));
            chosen.Add(TurretFootprint(c, q.Size));
        }
        var verified = candidates.Select(c => c.Cell).ToList();
        var (n, gate) = TurretLimit(R, verified.Count);
        if (n == 0)
        {
            tier = tier with { Gated = verified.Count == 0 ? "" : gate };
            return (tier, candidates);
        }
        var transmitters = new HashSet<Cell>(q.Transmitters);
        // Conduits may run under player walls, standing or planned, and cover but never on a lane, a trap cell,
        // a protected walkway or unknown ground.
        var underWall = new HashSet<Cell>(g.Walls);
        var allowed = new HashSet<Cell>();
        foreach (var (c, row) in Cells)
        {
            if ((taken.Contains(c) && !underWall.Contains(c)) || Protect.Contains(c) || Positive(row.Door) || Positive(row.NaturalRock))
            {
                continue;
            }
            if (Positive(row.Walkable) || (Positive(row.PlayerOwned) && row.Edifice != ""))
            {
                allowed.Add(c);
            }
        }
        foreach (var c in transmitters)
        {
            allowed.Add(c);
        }
        // No conduit runs under a turret's footprint.
        foreach (var t in verified.Take(n))
        {
            foreach (var c in TurretFootprint(t, q.Size))
            {
                allowed.Remove(c);
            }
        }
        var buildings = new List<Building>();
        foreach (var t in verified.Take(n))
        {
            buildings.Add(new Building(q.Definition, t, Rotation.North, q.Stuff));
            var chain = ConduitChain(t, transmitters, allowed);
            if (chain is null)
            {
                // No route to the network: the turret would stand dark, so the tier stops at the turrets it can power.
                buildings.RemoveAt(buildings.Count - 1);
                gate = GateTurretRoute;
                break;
            }
            foreach (var c in chain)
            {
                buildings.Add(new Building(q.Conduit, c, Rotation.North, ""));
                transmitters.Add(c);
            }
        }
        // Conduit cost is in the budget only once the chains are known, so the tier is re-checked against stock with them included.
        while (buildings.Count > 0 && !Affordable(R, buildings))
        {
            buildings = TrimLastTurret(buildings, q.Definition);
            gate = GateTurretStock;
        }
        // Each turret reserves its whole footprint.
        var reserved = new List<Cell>();
        foreach (var b in buildings)
        {
            if (b.Definition == q.Definition)
            {
                reserved.AddRange(TurretFootprint(b.Cell, q.Size));
            }
        }
        tier = tier with
        {
            Gated = gate,
            Reserved = EquatableArray.From(reserved),
            Buildings = EquatableArray.From(buildings),
            Costs = TierCosts(R.UnitCosts, buildings),
        };
        return (tier, candidates);
    }

    /// <summary>
    /// The shortest conduit run that brings a transmitter within native connector reach of the turret,
    /// cardinally chained to an existing transmitter; empty when one is already in reach; null when no run exists.
    /// </summary>
    internal static List<Cell>? ConduitChain(Cell turret, HashSet<Cell> transmitters, HashSet<Cell> allowed)
    {
        foreach (var c in transmitters)
        {
            if (Chebyshev(c, turret) <= ConduitReach)
            {
                return [];
            }
        }
        if (transmitters.Count == 0)
        {
            return null;
        }
        allowed.Add(turret);
        var path = PowerRoute(turret, transmitters, allowed);
        allowed.Remove(turret);
        if (path.Count < 2)
        {
            return null;
        }
        // PowerRoute returns destination first; reverse to turret first.
        path.Reverse();
        var start = 1;
        for (var i = path.Count - 2; i >= 1; i--)
        {
            if (Chebyshev(path[i], turret) <= ConduitReach)
            {
                start = i;
                break;
            }
        }
        return path.GetRange(start, path.Count - 1 - start);
    }
}

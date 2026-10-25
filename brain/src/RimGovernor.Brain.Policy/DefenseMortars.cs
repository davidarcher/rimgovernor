namespace RimGovernor.Brain.Policy;

/// <summary>
/// The mortar tier's input. Available is the native planning definition's availability re-checked against
/// finished research, Stock the colony's stock census and Max the budget MortarBudget buys from the observed
/// raid points. Definition empty asks for no mortar tier.
/// </summary>
public sealed record DefenseMortarRequest
{
    public string Definition { get; init; } = "";

    public string Stuff { get; init; } = "";

    public bool Available { get; init; }

    public IReadOnlyDictionary<Resource, long> Stock { get; init; } = new Dictionary<Resource, long>();

    public int Max { get; init; }
}

public static class DefenseMortarsPolicy
{
    public static readonly DefenseTierName TierMortars = new("mortars");

    // MortarBehindMin and MortarBehindMax bound how far behind the shooters' row a mortar stands: far enough
    // that a destroyed mortar's blast misses the line, near enough to stay on defended ground.
    internal const int MortarBehindMin = 4;
    internal const int MortarBehindMax = 10;

    /// <summary>
    /// One mortar while the raid shortfall stands and a turret line exists to place it behind; none otherwise.
    /// </summary>
    public static int MortarBudget(RaidDefense threat, IReadOnlyList<DefenseTurretFacts> turrets) =>
        DefenseBudget.TurretsOwed(threat, turrets) > 0 && DefenseBudget.TurretsStanding(turrets) > 0 ? 1 : 0;

    /// <summary>Reports whether the observed gates (available definition, budget, stock for one mortar) allow at least one mortar before any site is read.</summary>
    public static bool MortarGatesOpen(this DefenseRequest r) => MortarGate(r, 1) > 0;

    internal static int MortarGate(DefenseRequest r, int sites) => MortarLimit(r, sites).Count;

    /// <summary>MortarGate with the gate that cut the count below the sites ("" when nothing did): the threat demand (Max), then stock.</summary>
    internal static (int Count, string Gate) MortarLimit(DefenseRequest r, int sites)
    {
        var q = r.Mortar;
        if (q.Definition == "" || !q.Available)
        {
            return (0, DefenseTurretsPolicy.GateMortarUnavailable);
        }
        if (q.Max <= 0)
        {
            return (0, DefenseTurretsPolicy.GateMortarThreat);
        }
        var n = q.Max;
        var gate = DefenseTurretsPolicy.GateMortarThreat;
        if (n >= sites)
        {
            n = sites;
            gate = "";
        }
        for (; n > 0; n--)
        {
            var mortars = new List<Building>();
            for (var i = 0; i < n; i++)
            {
                var b = DefenseIEDs.TryBuilding(q.Definition, default, q.Stuff);
                if (b is null)
                {
                    return (0, DefenseTurretsPolicy.GateMortarUnavailable);
                }
                mortars.Add(b);
            }
            if (DefenseLayout.StockCovers(r.UnitCosts, q.Stock, mortars))
            {
                break;
            }
            gate = DefenseTurretsPolicy.GateMortarStock;
        }
        return (n, gate);
    }

    /// <summary>
    /// Proposes the mortar tier for a layout: mortars behind the shooters' row (away from the corridor entry),
    /// on free ground known to be unroofed, off the lanes and every reserved cell, spaced from the shooters, the
    /// turrets and each other so a destroyed one takes nothing with it. It needs no line of sight.
    /// </summary>
    public static DefenseTier DefenseMortars(DefenseRequest r, DefenseGeometry g)
    {
        var s = DefenseSite.Create(r);
        var tier = new DefenseTier { Name = TierMortars };
        var q = r.Mortar;
        if (q.Definition == "" || g.Firing.Count == 0)
        {
            return tier;
        }
        var d = DefenseTurretsPolicy.DirectionOf(g.Toward);
        var p = DefenseLayout.Perpendicular(d);
        var taken = new HashSet<Cell>(g.Lanes);
        taken.UnionWith(g.Reserved);
        taken.UnionWith(g.Turrets);
        var spaced = new List<Cell>(g.Firing);
        spaced.AddRange(g.Turrets);
        var origin = g.Firing[0];
        int low = 0, high = 0;
        foreach (var f in g.Firing)
        {
            var o = ((f.X - origin.X) * p.X) + ((f.Z - origin.Z) * p.Z);
            low = Math.Min(low, o);
            high = Math.Max(high, o);
        }
        var mid = (low + high) / 2;
        var sites = new List<Cell>();
        for (var behind = MortarBehindMin; behind <= MortarBehindMax; behind++)
        {
            var row = DefenseLayout.AddCell(origin, DefenseLayout.Scale(d, behind));
            // The centre of the firing span first, then outward.
            for (var w = 0; w <= DefenseLayout.DefenseMaxWidth; w++)
            {
                int[] offsets = w == 0 ? [mid] : [mid + w, mid - w];
                foreach (var o in offsets)
                {
                    var c = DefenseLayout.AddCell(row, DefenseLayout.Scale(p, o));
                    if (!s.Cells.TryGetValue(c, out var cell) || taken.Contains(c) || !s.Free(c) || cell.Roofed)
                    {
                        continue;
                    }
                    if (TooClose(c, spaced) || TooClose(c, sites))
                    {
                        continue;
                    }
                    sites.Add(c);
                }
            }
        }
        var (n, gate) = MortarLimit(r, sites.Count);
        if (sites.Count > 0)
        {
            tier = tier with { Gated = gate };
        }
        var buildings = new List<Building>();
        var reserved = new List<Cell>();
        foreach (var c in sites.Take(n))
        {
            buildings.Add(new Building(q.Definition, c, Rotation.North, q.Stuff));
            reserved.Add(c);
        }
        return tier with
        {
            Buildings = EquatableArray.From(buildings),
            Reserved = EquatableArray.From(reserved),
        };
    }

    /// <summary>Reports a cell within turret spacing of any of the cells.</summary>
    internal static bool TooClose(Cell c, IEnumerable<Cell> cells)
    {
        foreach (var o in cells)
        {
            if (LightingRules.Chebyshev(c, o) < DefenseTurretsPolicy.TurretSpacing)
            {
                return true;
            }
        }
        return false;
    }
}

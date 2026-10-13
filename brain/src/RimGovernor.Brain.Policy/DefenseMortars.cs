namespace RimGovernor.Brain.Policy;

/// <summary>
/// The mortar tier's input. Available is the native planning definition's availability re-checked against
/// finished research, Stock the colony's stock census and Max the budget MortarBudget buys from the observed
/// raid points. An unknown gate places no mortar; Definition empty asks for no mortar tier.
/// </summary>
public sealed record DefenseMortarRequest
{
    public string Definition { get; init; } = "";

    public string Stuff { get; init; } = "";

    public Fact<bool> Available { get; init; }

    public Fact<IReadOnlyDictionary<Resource, long>> Stock { get; init; }

    public int Max { get; init; }
}

public static partial class PolicyFunctions
{
    public static readonly DefenseTierName TierMortars = new("mortars");

    // MortarBehindMin and MortarBehindMax bound how far behind the shooters' row a mortar stands: far enough
    // that a destroyed mortar's blast misses the line, near enough to stay on defended ground.
    internal const int MortarBehindMin = 4;
    internal const int MortarBehindMax = 10;

    /// <summary>The raid-point reading at which a second mortar is budgeted; the first comes with the turret budget's top band.</summary>
    internal const double MortarHighPoints = ArmoryFabricationPoints;

    /// <summary>
    /// The number of mortars the observed raid points demand: none while they are unknown or below
    /// TurretHighPoints, then one, plus one more for every MortarHighPoints-TurretHighPoints above that.
    /// </summary>
    public static int MortarBudget(Fact<double> raidPoints)
    {
        if (!raidPoints.TryGetValue(out var points) || double.IsNaN(points) || double.IsInfinity(points) || points < TurretHighPoints)
        {
            return 0;
        }
        return 1 + (int)((points - TurretHighPoints) / (MortarHighPoints - TurretHighPoints));
    }

    /// <summary>Reports whether the observed gates (available definition, budget, stock for one mortar) allow at least one mortar before any site is read.</summary>
    public static bool MortarGatesOpen(this DefenseRequest r) => MortarGate(r, 1) > 0;

    internal static int MortarGate(DefenseRequest r, int sites) => MortarLimit(r, sites).Count;

    /// <summary>MortarGate with the gate that cut the count below the sites ("" when nothing did): the threat demand (Max), then stock.</summary>
    internal static (int Count, string Gate) MortarLimit(DefenseRequest r, int sites)
    {
        var q = r.Mortar;
        if (q.Definition == "" || !q.Available.TryGetValue(out var available) || !available)
        {
            return (0, GateMortarUnavailable);
        }
        if (q.Max <= 0)
        {
            return (0, GateMortarThreat);
        }
        var n = q.Max;
        var gate = GateMortarThreat;
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
                var b = TryBuilding(q.Definition, default, q.Stuff);
                if (b is null)
                {
                    return (0, GateMortarUnavailable);
                }
                mortars.Add(b);
            }
            if (StockCovers(r.UnitCosts, q.Stock, mortars))
            {
                break;
            }
            gate = GateMortarStock;
        }
        return (n, gate);
    }

    /// <summary>Reports whether the stock covers the buildings' summed unit costs; unknown costs or stock never cover anything.</summary>
    internal static bool StockCovers(IReadOnlyDictionary<string, EquatableArray<Amount>> unitCosts, Fact<IReadOnlyDictionary<Resource, long>> stock, IEnumerable<Building> buildings)
    {
        if (!TierCosts(unitCosts, buildings).TryGetValue(out var costs) || !stock.TryGetValue(out var have))
        {
            return false;
        }
        foreach (var a in costs)
        {
            if (have.GetValueOrDefault(a.Resource) < a.Count)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Proposes the mortar tier for a layout: mortars behind the shooters' row (away from the corridor entry),
    /// on free ground known to be unroofed, off the lanes and every reserved cell, spaced from the shooters, the
    /// turrets and each other so a destroyed one takes nothing with it. It needs no line of sight.
    /// </summary>
    public static DefenseTier DefenseMortars(DefenseRequest r, DefenseGeometry g)
    {
        var s = DefenseSite.Create(r);
        var tier = new DefenseTier { Name = TierMortars, Costs = Known(EquatableArray.From(new List<Amount>())) };
        var q = r.Mortar;
        if (q.Definition == "" || g.Firing.Count == 0)
        {
            return tier;
        }
        var d = DirectionOf(g.Toward);
        var p = Perpendicular(d);
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
            var row = AddCell(origin, Scale(d, behind));
            // The centre of the firing span first, then outward.
            for (var w = 0; w <= DefenseMaxWidth; w++)
            {
                int[] offsets = w == 0 ? [mid] : [mid + w, mid - w];
                foreach (var o in offsets)
                {
                    var c = AddCell(row, Scale(p, o));
                    if (!s.Cells.TryGetValue(c, out var cell) || taken.Contains(c) || !s.Free(c) || !Negative(cell.Roofed))
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
            Costs = TierCosts(r.UnitCosts, buildings),
        };
    }

    /// <summary>Reports a cell within turret spacing of any of the cells.</summary>
    internal static bool TooClose(Cell c, IEnumerable<Cell> cells)
    {
        foreach (var o in cells)
        {
            if (Chebyshev(c, o) < TurretSpacing)
            {
                return true;
            }
        }
        return false;
    }
}

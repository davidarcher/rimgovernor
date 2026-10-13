namespace RimGovernor.Brain.Policy;

/// <summary>
/// One stockpile zone for the stock overlay: every stockpile on the map. Role and Filter are the colony's claim
/// on it when it created the zone (Filter known then); FoodStorage is the zone census' human-food flag; Label is
/// the native zone label.
/// </summary>
public sealed record StockZone
{
    public string Id { get; init; } = "";

    public string Role { get; init; } = "";

    public string Label { get; init; } = "";

    public Fact<StockpileFilter> Filter { get; init; }

    public bool FoodStorage { get; init; }

    public IReadOnlyList<Cell> Cells { get; init; } = [];

    /// <summary>
    /// Whether the zone is a food store, a general store, and the definitions an allow-only filter names.
    /// </summary>
    internal (bool Food, bool General, HashSet<Resource>? Defs) Stores()
    {
        var prefix = Role.Split(':', 2)[0];
        var food = FoodStorage || prefix == "meals" || prefix == "rawfood" || prefix == "ingredients";
        var general = IsWarehouseRole(Role);
        if (!Filter.TryGetValue(out var filter))
        {
            return (food, general, null);
        }
        switch (filter.Base)
        {
            case FilterBase.Food:
            case FilterBase.Perishables:
                food = true;
                break;
            case FilterBase.Everything:
            case FilterBase.Nonperishables:
            case FilterBase.IndoorOnly:
                general = true;
                break;
        }
        HashSet<Resource>? defs = null;
        if (filter.TryGetAllowOnlyDefinitions(out var names))
        {
            defs = [];
            foreach (var n in names)
            {
                defs.Add(new Resource(n));
            }
        }
        return (food, general, defs);
    }

    internal bool Medicine() => Role.Split(':', 2)[0] == "medicine";
}

/// <summary>
/// The targets a zone is tinted against: the review's effective resource targets with the census' stock of
/// each, and stored food in days against the seasonal target.
/// </summary>
public sealed record StockLevels
{
    public IReadOnlyDictionary<Resource, long> Targets { get; init; } = new Dictionary<Resource, long>();

    public IReadOnlyDictionary<Resource, long> Stock { get; init; } = new Dictionary<Resource, long>();

    public Fact<double> FoodDays { get; init; }

    public double FoodTargetDays { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>The overlay layer the stockpile shortfall tint draws on.</summary>
    public const string StockLayer = "stock";

    /// <summary>The food target's name: stored food in days against the seasonal food target, not a definition count.</summary>
    private static readonly Resource StockFood = new("food");

    /// <summary>Shortfall bands: under this fill of the target is short (red), under the target amber, at or over it met (green).</summary>
    private const double StockShortFill = 0.5;

    /// <summary>
    /// Tints every stockpile zone by the fill of the worst-covered target it stores (red short, amber, green
    /// met); it draws no text labels, and a zone storing nothing with a target is left untinted. A zone stores a
    /// target when its filter allows only that definition, when its role or food flag makes it a food store
    /// (food days), when its role is a medicine store (every Medicine* target), or when it is a general store
    /// (role general, or an everything or non-perishables filter: every non-food target).
    /// </summary>
    public static LayoutOverlay StockOverlay(IReadOnlyList<StockZone> zones, StockLevels levels, Bounds bounds)
    {
        var sorted = zones.OrderBy(z => z.Id, StringComparer.Ordinal);
        var bands = new List<Cell>[] { [], [], [] };
        foreach (var z in sorted)
        {
            var cells = z.Cells.Where(c => InBounds(c, bounds)).ToList();
            if (cells.Count == 0)
            {
                continue;
            }
            var (_, have, target, ok) = WorstStock(z, levels);
            if (!ok)
            {
                continue;
            }
            var band = 2;
            var fill = have / target;
            if (fill < StockShortFill)
            {
                band = 0;
            }
            else if (fill < 1)
            {
                band = 1;
            }
            bands[band].AddRange(cells);
        }
        var layers = new List<OverlayLayer>();
        string[] names = ["short", "low", "met"];
        var hues = new[] { PlanRed, PlanAmber, PlanGreen };
        for (var i = 0; i < hues.Length; i++)
        {
            if (bands[i].Count > 0)
            {
                layers.Add(new OverlayLayer { Color = hues[i].Fill(), Style = OverlayFill, Label = names[i], Runs = CellRuns(bands[i]) });
            }
        }
        return new LayoutOverlay { Layers = layers };
    }

    /// <summary>The zone's stored target with the lowest fill.</summary>
    private static (Resource Best, double Have, double Target, bool Found) WorstStock(StockZone z, StockLevels levels)
    {
        var best = default(Resource);
        double have = 0;
        double target = 0;
        var found = false;

        void Consider(Resource r, double h, double t)
        {
            if (t <= 0)
            {
                return;
            }
            if (!found || h / t < have / target || (h / t == have / target && string.CompareOrdinal(r.Value, best.Value) < 0))
            {
                (best, have, target, found) = (r, h, t, true);
            }
        }

        var (food, general, defs) = z.Stores();
        if (levels.FoodDays.TryGetValue(out var days) && food)
        {
            Consider(StockFood, days, levels.FoodTargetDays);
        }
        foreach (var (r, t) in levels.Targets)
        {
            if (general || (defs is not null && defs.Contains(r)) || (z.Medicine() && r.Value.StartsWith("Medicine", StringComparison.Ordinal)))
            {
                levels.Stock.TryGetValue(r, out var stock);
                Consider(r, stock, t);
            }
        }
        return (best, have, target, found);
    }
}

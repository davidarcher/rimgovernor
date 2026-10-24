using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>How a construction material's per-unit value was found (#3653).</summary>
public enum MaterialValueBasis
{
    /// <summary>No acquisition source and no market value: the material is charged nothing (material_value_unpriced).</summary>
    Unpriced = 0,

    /// <summary>The replacement labor of one more unit from the nearest reachable source: its chop or mine work and its haul.</summary>
    Labor,

    /// <summary>The market value converted to work (material_value_market).</summary>
    Market,
}

/// <summary>One construction material's value in capability-days per unit, and how it was found (#3653).</summary>
public sealed record ConstructionMaterialValue(MaterialValueBasis Basis, double PerUnit);

// The construction material term of the Investment ledger (#3653, #3647 decision 2): a priced planned room's deliveries
// are charged the material they consume, at the work it takes to replace a unit, or its market value where nothing in
// reach yields it.
public static partial class ForecastValue
{
    /// <summary>The Investment key of a construction material's consumption.</summary>
    public static string MaterialKey(Resource def) => $"material/{def}";

    /// <summary>
    /// The activities whose consumed inputs the ledger charges as material: the composed realizations of the planned sites
    /// of priced rooms (<see cref="PlannedRoomCredits.Priced"/>: their role has a credit, #3796). Every other construction root,
    /// placed sites included, stays free until its role is priced (#3653 decision 1). Of a site's realizations only its
    /// deliveries consume anything; a refuel or support draw on the same stack is another activity and is not charged.
    /// </summary>
    internal static HashSet<string> ChargedMaterialActivities(ForecastCoupledContext context)
    {
        var charged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var site in context.PlannedSites)
            if (context.Priced(site.Room))
                charged.UnionWith(site.Activities);
        return charged;
    }

    /// <summary>Each shelter's fueled sources with a known burn (#3673), whose fuel the ledger charges per source (#3679).</summary>
    internal static IEnumerable<(ForecastShelterPlan Shelter, ThermalSource Source)> FueledShelters(ForecastCoupledContext context) =>
        context.Shelters.SelectMany(s => s.Sources.Where(f => f.Fuel.Length != 0 && f.FuelPerTick.IsKnown).Select(f => (s, f)));

    /// <summary>The materials the charged activities consume and the fueled shelters burn, each priced once per review (<see cref="MaterialValueOf"/>).</summary>
    public static IReadOnlyList<Resource> ChargedMaterials(ForecastCoupledContext context)
    {
        var charged = ChargedMaterialActivities(context);
        return [.. context.Activities.Activities.Where(a => charged.Contains(a.Id)).SelectMany(a => a.Inputs).Select(i => i.Resource)
            .Concat(FueledShelters(context).Select(s => new Resource(s.Source.Fuel)))
            .Distinct().OrderBy(r => r.Value, StringComparer.Ordinal)];
    }

    /// <summary>
    /// One material's value per unit. Replacement labor, when the catalog holds its chop or mine work and yield
    /// (<paramref name="haul"/>) and <paramref name="sources"/> has one in reach: the work over the yield, plus one haul
    /// trip (the nearest reachable source's distance at <see cref="DefenseCorridorRoutes.PathMoveCardinal"/> ticks a cell)
    /// over its load, min(stack limit, max(1, yield)). Otherwise its market value over
    /// <see cref="SilverGapRules.ValuePerWorkTick"/>; a block is always priced so. Unpriced with neither. Null
    /// <paramref name="sources"/> is a failed read. One work tick is one capability-tick.
    /// </summary>
    public static ConstructionMaterialValue MaterialValueOf(Resource def, IReadOnlyList<ResourceSource>? sources, ResourceStorage? storage,
        HaulModel haul, ItemFacts items)
    {
        var acquired = haul.Chopped.Concat(haul.Mined).FirstOrDefault(s => s.Good == def && s.Valid());
        var nearest = sources?.Where(s => s.Reachable == Known(true) && !s.Buried
            && (s.Method != ResourceSourceMethod.Mine || ResourceRules.MineSafe(s.Safety))).OrderBy(s => s.Distance).FirstOrDefault();
        if (acquired is not null && nearest is not null && storage is { StackLimit: > 0 } store && double.IsFinite(nearest.Distance) && nearest.Distance >= 0)
        {
            var load = Math.Min(store.StackLimit, Math.Max(1, acquired.Units));
            var ticks = (acquired.Work / acquired.Units) + (nearest.Distance * DefenseCorridorRoutes.PathMoveCardinal / load);
            return new(MaterialValueBasis.Labor, ticks / GameTime.TicksPerDay);
        }
        return items.TryMarketValue(def, out var market) && market > 0
            ? new(MaterialValueBasis.Market, market / SilverGapRules.ValuePerWorkTick / GameTime.TicksPerDay)
            : new(MaterialValueBasis.Unpriced, 0);
    }
}

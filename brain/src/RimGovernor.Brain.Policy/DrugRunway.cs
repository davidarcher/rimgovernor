namespace RimGovernor.Brain.Policy;

/// <summary>One resource's runway: the observed use a day, the stock and the days of use that stock covers (zero when none is observed).</summary>
public sealed record StockRunway(Resource Resource, double PerDay, long Stock, double StockDays, double ShortfallDays);

/// <summary>One resource-runway domain of the forward projection (drugs, materials, medicines). ShortfallDays is the largest per-resource shortfall.</summary>
public sealed record StockProjection(EquatableArray<StockRunway> Resources, double ShortfallDays);

// Drug runway. A social drug is spent where a colonist takes it; the realized-consumption ring counts each dose,
// and ForecastResourceRunway turns the observed rate and the stock into days left.
public static partial class PolicyFunctions
{
    /// <summary>
    /// The runway keys of the catalog's social drugs, each at one dose per colonist whose drug policy permits it
    /// (users), so a drug nobody has taken yet is still stocked; once doses are taken the observed rate takes over.
    /// </summary>
    public static Dictionary<Resource, long> DrugRunwayReserves(ItemFacts items, IReadOnlyDictionary<Resource, long> users)
    {
        var output = new Dictionary<Resource, long>();
        foreach (var drug in items.RecreationDrugs())
        {
            output[drug.Def] = Math.Max(0, users.GetValueOrDefault(drug.Def));
        }
        return output;
    }

    /// <summary>Counts, for each social drug, the colonists holding a drug policy that allows it (joy, addiction or scheduled use).</summary>
    public static Dictionary<Resource, long> DrugUsers(ItemFacts items, IEnumerable<OwnedDrugPolicy> policies)
    {
        var social = items.RecreationDrugs();
        var output = new Dictionary<Resource, long>();
        foreach (var p in policies)
        {
            foreach (var entry in p.Entries)
            {
                var drug = new Resource(entry.Drug);
                if (!entry.Off && social.Any(d => d.Def == drug))
                {
                    output[drug] = output.GetValueOrDefault(drug) + p.Pawns.Count;
                }
            }
        }
        return output;
    }

    /// <summary>
    /// Reads the social drugs' rows out of the resource runways. No drug row or a drug whose use or stock is
    /// unobserved leaves the projection unknown; a drug observed unused has no shortfall.
    /// </summary>
    public static Fact<StockProjection> PlanDrugRunway(IEnumerable<ResourceRunway> rows, ItemFacts items)
    {
        var social = items.RecreationDrugs();
        return StockProjectionOf(rows, r => social.Any(d => d.Def == r));
    }

    /// <summary>
    /// The runway of each row the predicate keeps: the observed use a day, the stock and the days it covers, and
    /// the largest shortfall. No such row, or one whose use or stock is unobserved, is unknown.
    /// </summary>
    private static Fact<StockProjection> StockProjectionOf(IEnumerable<ResourceRunway> rows, Func<Resource, bool> keep)
    {
        var resources = new List<StockRunway>();
        var shortfall = 0.0;
        foreach (var row in rows)
        {
            if (!keep(row.Resource))
            {
                continue;
            }
            if (!row.ConsumptionPerDay.TryGetValue(out var rate) || !row.Stock.TryGetValue(out var stock) || !double.IsFinite(rate) || rate < 0)
            {
                return Unknown<StockProjection>();
            }
            double stockDays = 0, rowShortfall = 0;
            if (rate > 0)
            {
                stockDays = stock / rate;
                rowShortfall = RunwayShortfall(stockDays, ProjectionHorizonDays);
            }
            shortfall = Math.Max(shortfall, rowShortfall);
            resources.Add(new StockRunway(row.Resource, rate, stock, stockDays, rowShortfall));
        }
        if (resources.Count == 0)
        {
            return Unknown<StockProjection>();
        }
        var sorted = resources.OrderBy(r => r.Resource.Value, StringComparer.Ordinal);
        return Known(new StockProjection(EquatableArray.From(sorted), shortfall));
    }
}

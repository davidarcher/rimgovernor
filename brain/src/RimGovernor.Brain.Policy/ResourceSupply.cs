namespace RimGovernor.Brain.Policy;

/// <summary>
/// One unmet the economy/resource Need floor and the candidates that can cover it: mines, bench bills, chops,
/// harvests, hunts, deep drills and a caravan's offers.
/// </summary>
public sealed record ResourceSupplyInput
{
    public Resource Resource { get; init; }

    public long Deficit { get; init; }

    public EquatableArray<SupplyCandidate> Candidates { get; init; }

    /// <summary>How far ahead the deficit is wanted: a candidate with a longer lead (a field) is not eligible. Zero is wanted now.</summary>
    public double HorizonDays { get; init; }

    /// <summary>The stock the candidates' ingredient draws may spend (see UsableIngredients).</summary>
    public EquatableArray<ResourceQuantity> Usable { get; init; }
}

/// <summary>The Round's one supply plan over every resource deficit.</summary>
public sealed record ResourceSupply
{
    public SupplyPlan Plan { get; init; } = new();

    /// <summary>Lists the candidates the plan opened for resource, best first.</summary>
    public IReadOnlyList<SupplyEntry> Opened(Resource resource)
    {
        var output = new List<SupplyEntry>();
        foreach (var e in Plan.Portfolio)
        {
            if (e.Decision != SupplyDecision.Open)
            {
                continue;
            }
            foreach (var y in e.Candidate.Yields)
            {
                if (y.Good.Def == resource)
                {
                    output.Add(e);
                    break;
                }
            }
        }
        return output;
    }

    /// <summary>The ids of the opened candidates of one kind for resource, in rank order.</summary>
    public HashSet<string> OpenedIDs(Resource resource, CandidateKind kind)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in Opened(resource))
        {
            if (e.Candidate.Kind == kind)
            {
                ids.Add(e.Candidate.Id);
            }
        }
        return ids;
    }
}

public static partial class ResourceRules
{

    public static (long Shortfall, bool Known) IngredientShortfall(ResourceQuantity cost, IReadOnlyList<ResourceQuantity> usable)
    {
        var shortfall = cost.Count;
        foreach (var amount in usable)
            if (amount.Key == cost.Key) shortfall -= amount.Count;
        return (shortfall, usable.Any(amount => amount.Key == cost.Key));
    }

    public static long DerivedTarget(long stock, long shortfall) => stock + shortfall;

    public static long GrownPlantationYield(IReadOnlyList<AcquisitionSource> rows, Resource resource)
    {
        var units = 0.0;
        foreach (var s in rows)
        {
            if (s.Plantation && s.Tree && new Resource(s.Resource) == resource)
            {
                units += s.Yield;
            }
        }

        return (long)Math.Round(units, MidpointRounding.AwayFromZero);
    }

    /// <summary>Outstanding units after designated sources and the unharvested plantation are credited once.</summary>
    public static (long Need, long Standing, List<AcquisitionSource> Open, List<(AcquisitionSource Source, double Yield)> Priced) OutstandingSupply(
        Resource resource, long deficit, IReadOnlyList<AcquisitionSource> rows, IReadOnlySet<Resource> clothing,
        IReadOnlyDictionary<string, bool> held, int hunts, long standingFields)
    {
        var need = deficit;
        var open = new List<AcquisitionSource>();
        var priced = new List<(AcquisitionSource Source, double Yield)>();
        foreach (var source in rows)
        {
            var offered = ClothingDemandRules.ResourceYieldFor(source, resource, clothing);
            if (offered is null) continue;
            if (source.Designated) need -= (long)Math.Round(offered.Value, MidpointRounding.AwayFromZero);
            else if (!(held.TryGetValue(source.Id, out var isHeld) && isHeld) && !(source.Hunt && hunts <= 0))
            {
                open.Add(source);
                priced.Add((source, offered.Value));
            }
        }
        var standing = Math.Max(standingFields - GrownPlantationYield(rows, resource), 0);
        return (need - standing, standing, open, priced);
    }

    public static long OutstandingTarget(long target, long stock) => target - stock;

    public static long UncoveredSupply(long need) => Math.Max(need, 0);

    public static long PlannedTradeUnits(SupplyEntry entry)
    {
        entry.Candidate.Yields[0].StockOf(out var units);
        return entry.Wanted > 0 ? Math.Min(units, entry.Wanted) : units;
    }

    /// <summary>
    /// Runs PlanSupply over the deficits as stock demands wanted now, sharing one labor budget (work ticks per
    /// day; what the food plan leaves of workers * 20000). A candidate listed under two resources keeps its
    /// first listing.
    /// </summary>
    public static ResourceSupply PlanResourceSupply(IReadOnlyList<ResourceSupplyInput> inputs, double labor)
    {
        var demands = new List<SupplyDemand>();
        var candidates = new List<SupplyCandidate>();
        var usable = new Dictionary<ResourceKey, long>();
        var keys = new List<ResourceKey>();
        var seen = new HashSet<(string, string)>();
        foreach (var input in inputs)
        {
            if (input.Deficit <= 0)
            {
                continue;
            }
            var demand = SupplyPlanRules.SupplyDemandOfResource(new ResourceDemand { Key = new ResourceKey { Def = input.Resource }, Count = input.Deficit });
            demands.Add(demand with { HorizonDays = input.HorizonDays });
            foreach (var q in input.Usable)
            {
                if (!usable.TryGetValue(q.Key, out var have))
                {
                    keys.Add(q.Key);
                    usable[q.Key] = q.Count;
                }
                else
                {
                    usable[q.Key] = Math.Min(have, q.Count);
                }
            }
            foreach (var c in input.Candidates)
            {
                if (seen.Add((Wire.Name(c.Kind), c.Id)))
                {
                    candidates.Add(c);
                }
            }
        }
        var spendable = keys.Select(k => new ResourceQuantity { Key = k, Count = usable[k] }).ToList();
        var plan = SupplyPlanRules.PlanSupply(new SupplyPlanRequest
        {
            Demands = EquatableArray.From(demands),
            Candidates = EquatableArray.From(candidates),
            Labor = labor,
            Usable = EquatableArray.From(spendable),
        });
        return new ResourceSupply { Plan = plan };
    }
}

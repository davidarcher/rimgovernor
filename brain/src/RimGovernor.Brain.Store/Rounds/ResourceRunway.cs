using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

public sealed partial class Store
{
    /// <summary>
    /// Forecasts every medicine, social drug and resource the recurring spend native observed
    /// (<c>Facts.ResourceConsumption</c>) names, in resource order. A resource's stock is the census rows it appears in;
    /// its surface ore is the entry the ore read left for it, none without one.
    /// </summary>
    internal static EquatableArray<ResourceRunway> ResourceRunways(RoundsRequest r)
    {
        var facts = r.Facts;
        var reserves = r.Policy.RunwayReserves(facts.Items, facts.Colonists, facts.ResourceConsumption);
        foreach (var (resource, reserve) in DrugRunwayRules.DrugRunwayReserves(facts.Items, facts.DrugUsers ?? new Dictionary<Resource, long>(), facts.Preferences))
        {
            reserves[resource] = reserve;
        }
        var result = new List<ResourceRunway>();
        foreach (var resource in reserves.Keys.OrderBy(key => key.Value, StringComparer.Ordinal))
        {
            long stock = 0;
            foreach (var row in facts.Resources)
            {
                if (row.Resource != resource)
                {
                    continue;
                }
                if (row.Count < 0 || row.Count > long.MaxValue - stock)
                {
                    throw new DomainException($"resource census row {resource.Value} is negative or overflows");
                }
                stock += row.Count;
            }
            var ore = facts.ResourceSurfaceOre.GetValueOrDefault(resource);
            result.Add(ResourceRules.ForecastResourceRunway(resource, stock, ore, reserves[resource], r.Tick, facts.ResourceConsumption));
        }
        return EquatableArray.From(result);
    }
}

/// <summary>The persisted review read back in policy terms.</summary>
public static class RoundsRunwayMembers
{
    /// <summary>The review's runway forecasts lifted back into policy terms.</summary>
    public static EquatableArray<ResourceRunway> ResourceRunwayState(this Rounds r) =>
        EquatableArray.From(
            r.ResourceRunways.Select(row => new ResourceRunway
            {
                Resource = row.Resource,
                Tick = row.Tick,
                WindowDays = row.WindowDays,
                Reserve = row.Reserve,
                Stock = row.Stock,
                SurfaceOre = row.SurfaceOre,
                Rating = Store.RunwayRatingOf(row),
                Target = row.Target,
            })
        );
}

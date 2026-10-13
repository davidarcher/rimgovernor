using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Store;

public sealed partial class Store
{
    /// <summary>
    /// Forecasts every medicine, social drug and resource the recurring spend native observed
    /// (<c>Facts.ResourceConsumption</c>) names, in resource order (Go <c>resourceRunways</c>).
    /// </summary>
    internal static EquatableArray<ResourceRunway> ResourceRunways(RoundsRequest r)
    {
        var facts = r.Facts;
        var reserves = r.Policy.RunwayReserves(facts.Items, facts.Colonists, facts.ResourceConsumption);
        foreach (var (resource, reserve) in PolicyFunctions.DrugRunwayReserves(facts.Items, facts.DrugUsers ?? new Dictionary<Resource, long>()))
        {
            reserves[resource] = reserve;
        }
        var result = new List<ResourceRunway>();
        foreach (var resource in reserves.Keys.OrderBy(key => key.Value, StringComparer.Ordinal))
        {
            var stock = Unknown<long>();
            if (facts.Resources.TryGetValue(out var rows))
            {
                long total = 0;
                var valid = true;
                foreach (var row in rows)
                {
                    if (row.Resource != resource)
                    {
                        continue;
                    }
                    if (row.Count < 0 || row.Count > long.MaxValue - total)
                    {
                        valid = false;
                        break;
                    }
                    total += row.Count;
                }
                if (valid)
                {
                    stock = Known(total);
                }
            }
            var ore = facts.ResourceSurfaceOre is { } surface && surface.TryGetValue(resource, out var known) ? known : default;
            result.Add(PolicyFunctions.ForecastResourceRunway(resource, stock, ore, reserves[resource], r.Tick, facts.ResourceConsumption));
        }
        return EquatableArray.From(result);
    }
}

/// <summary>The persisted review read back in policy terms (Go <c>Rounds.ResourceRunwayState</c>).</summary>
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
                Stock = PersistedFact.Of(row.Stock),
                SurfaceOre = PersistedFact.Of(row.SurfaceOre),
                ConsumptionPerDay = PersistedFact.Of(row.ConsumptionPerDay),
                StockDays = PersistedFact.Of(row.StockDays),
                DaysLeft = PersistedFact.Of(row.DaysLeft),
                Deficit = PersistedFact.Of(row.Deficit),
                Target = row.Target,
            })
        );
}

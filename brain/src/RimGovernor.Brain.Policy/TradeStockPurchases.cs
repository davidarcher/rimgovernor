using System.Collections.Concurrent;

namespace RimGovernor.Brain.Policy;

/// <summary>One priced unit in a forecast or a live sheet; availability and capacity are observed by the caller.</summary>
public sealed record StockPurchaseOffer(string Id, Resource Resource, long Available, double Price, double Mass);
public sealed record StockPurchase(string Id, Resource Resource, long Count);

/// <summary>The one purchase rule for selected stock waits: medicine first, one silver and mass budget across every row.</summary>
public static class TradeStockPurchases
{
    public static EquatableArray<StockPurchase> Plan(IReadOnlyList<StockNeed> needs, IReadOnlyList<Resource> medicines,
        IEnumerable<StockPurchaseOffer> offers, double silver, double capacity = double.PositiveInfinity)
    {
        if (!double.IsFinite(silver) || silver < 0 || double.IsNaN(capacity) || capacity < 0)
            throw new RequiredFactException("stock_purchase_budget");
        var rows = offers.OrderBy(o => o.Price).ThenBy(o => o.Resource.Value, StringComparer.Ordinal).ThenBy(o => o.Id, StringComparer.Ordinal).ToArray();
        if (rows.Any(o => !double.IsFinite(o.Price) || o.Price <= 0 || !double.IsFinite(o.Mass) || o.Mass < 0 || o.Available < 0))
            throw new RequiredFactException("stock_purchase_offer");
        var bought = new Dictionary<string, long>(StringComparer.Ordinal);
        var held = new Dictionary<Resource, long>();
        foreach (var need in needs.OrderByDescending(n => n.AnyOf.Any(medicines.Contains))
            .ThenBy(n => string.Join("/", n.AnyOf.Select(r => r.Value).Order(StringComparer.Ordinal)), StringComparer.Ordinal))
        {
            // Rules may wait on the same replacement in several mutually exclusive harm branches. One on-hand
            // count serves that wait; these are stock targets, not additive promises of simultaneous surgeries.
            var missing = Math.Max(0, need.Count - need.AnyOf.Sum(r => held.GetValueOrDefault(r)));
            foreach (var row in rows.Where(o => need.AnyOf.Contains(o.Resource)))
            {
                var count = Math.Min(missing, Math.Min(row.Available - bought.GetValueOrDefault(row.Id), (long)Math.Min(long.MaxValue / 2d, Math.Floor(silver / row.Price))));
                if (row.Mass > 0) count = Math.Min(count, (long)Math.Min(long.MaxValue / 2d, Math.Floor(capacity / row.Mass)));
                if (count <= 0) continue;
                bought[row.Id] = bought.GetValueOrDefault(row.Id) + count;
                held[row.Resource] = held.GetValueOrDefault(row.Resource) + count;
                missing -= count; silver -= count * row.Price; capacity -= count * row.Mass;
                if (missing == 0) break;
            }
        }
        return EquatableArray.From(rows.Where(r => bought.GetValueOrDefault(r.Id) > 0).Select(r => new StockPurchase(r.Id, r.Resource, bought[r.Id])));
    }

    /// <summary>The waits the selected schedule's actual raid and illness rules chose, over its investment horizon.
    /// Reuses those rules and their distributions rather than rebuilding demand from unrelated resource targets.</summary>
    public static EquatableArray<StockNeed> Waits(ForecastCoupledOutcome outcome, ForecastCoupledContext context,
        IReadOnlyList<ForecastCommitment> chosen, double horizon)
    {
        ConcurrentDictionary<(string, string, EquatableArray<GearOption>, ForecastSupportConsequences, HitRange), HarmDistribution> fights = new();
        ConcurrentDictionary<(string, ForecastSupportConsequences?), HarmDistribution> illnesses = new();
        _ = HarmLedger.Of(outcome, context, horizon, fights, context.Needs.Pawns.ToDictionary(p => p.Pawn, StringComparer.Ordinal), chosen, illnesses);
        var needs = fights.Values.Concat(illnesses.Values).SelectMany(d => d.Replacements)
            .Where(r => r.Probability > 0 && r.Wait is not null).SelectMany(r => r.Wait!.Needs).ToList();
        needs.AddRange(context.Needs.Pawns.SelectMany(p => CreepJoinerHarm.Surgeries(context, p, HarmConsequences.CareOf(outcome.Support), horizon))
            .Where(r => r.Probability > 0 && r.Wait is not null).SelectMany(r => r.Wait!.Needs));
        if (context.Stock.MedicineReplenish > 0) needs.Insert(0, new(context.Stock.Medicines, context.Stock.MedicineReplenish));
        return EquatableArray.From(needs.Distinct());
    }
}

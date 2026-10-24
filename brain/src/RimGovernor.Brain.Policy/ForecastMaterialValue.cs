namespace RimGovernor.Brain.Policy;

/// <summary>Supplied marginal utility for one unmet need; units and utility must be comparable across options.</summary>
public sealed record ForecastNeedValue(double Remaining, double UtilityPerUnit);
public sealed record ForecastNeedRelief(string Need, double Units);
/// <summary>Effects and total labor of an entire batch with one exact ingredient filter, not per-item values.</summary>
public sealed record ForecastProductionEstimate(EquatableArray<ForecastNeedRelief> Relief, double WorkTicks);
/// <summary>A supplied valuation: the needs it values, the materials it prices and the labor rate. A need or material it
/// leaves out is not valued, so a plan that relieves or consumes it is not compared.</summary>
public sealed record ForecastMaterialValuation(
    IReadOnlyDictionary<string, ForecastNeedValue> Needs,
    IReadOnlyDictionary<Resource, double> MaterialUtilityPerUnit,
    double WorkUtilityPerTick);
public sealed record ForecastProductionValuation(ForecastMaterialValuation Values,
    IReadOnlyDictionary<string, ForecastProductionEstimate> ByOrderKey);
public sealed partial record ForecastMaterialValue(double Benefit, double MaterialCost, double WorkCost)
{
    public double Net => Benefit - MaterialCost - WorkCost;
}

public sealed partial record ForecastMaterialValue
{
    // A plan the valuation does not cover is not valued: never a fall back to batch count or zero.
    internal static ForecastMaterialValue? ValueMaterialPlan(IEnumerable<ForecastMaterialOption> options,
        ForecastMaterialValuation valuation, SortedSet<string> findings)
    {
        var relief = new Dictionary<string, double>(StringComparer.Ordinal);
        double materials = 0, work = 0, benefit = 0;
        static double Valid(double value, string label) =>
            double.IsFinite(value) && value >= 0 ? value : throw new DomainException("invalid production valuation: " + label);
        foreach (var option in options)
        {
            if (option.Estimate is not { } estimate) { findings.Add("unestimated_batch:" + option.Id); return null; }
            var ticks = Valid(estimate.WorkTicks, "work:" + option.Id);
            if (ticks > 0) work += ticks * Valid(valuation.WorkUtilityPerTick, "work_value");
            foreach (var cost in option.Cost.Where(c => c.Count > 0))
            {
                if (!valuation.MaterialUtilityPerUnit.TryGetValue(cost.Resource, out var rate))
                { findings.Add("material_unpriced:" + cost.Resource.Value); return null; }
                materials += cost.Count * Valid(rate, "material_value:" + cost.Resource.Value);
            }
            foreach (var effect in estimate.Relief)
            {
                if (string.IsNullOrWhiteSpace(effect.Need)) throw new DomainException("empty production need");
                relief[effect.Need] = relief.GetValueOrDefault(effect.Need) + Valid(effect.Units, "relief:" + effect.Need);
            }
        }
        foreach (var pair in relief.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!double.IsFinite(pair.Value)) throw new DomainException("production relief overflow");
            if (pair.Value == 0) continue;
            if (!valuation.Needs.TryGetValue(pair.Key, out var need)) { findings.Add("need_unvalued:" + pair.Key); return null; }
            benefit += Math.Min(Valid(need.Remaining, "need_remaining:" + pair.Key), pair.Value) * Valid(need.UtilityPerUnit, "need_value:" + pair.Key);
        }
        var result = new ForecastMaterialValue(benefit, materials, work);
        if (!double.IsFinite(result.Net) || !double.IsFinite(benefit) || !double.IsFinite(materials) || !double.IsFinite(work))
            throw new DomainException("production valuation overflow");
        return result;
    }
}

namespace RimGovernor.Brain.Policy;

/// <summary>Supplied marginal utility for one unmet need; units and utility must be comparable across options.</summary>
public sealed record ForecastNeedValue(Fact<double> Remaining, Fact<double> UtilityPerUnit);
public sealed record ForecastNeedRelief(string Need, Fact<double> Units);
/// <summary>Effects and total labor of an entire batch with one exact ingredient filter, not per-item values.</summary>
public sealed record ForecastProductionEstimate(EquatableArray<ForecastNeedRelief> Relief, Fact<double> WorkTicks);
public sealed record ForecastMaterialValuation(
    IReadOnlyDictionary<string, ForecastNeedValue> Needs,
    IReadOnlyDictionary<Resource, Fact<double>> MaterialUtilityPerUnit,
    Fact<double> WorkUtilityPerTick);
public sealed record ForecastProductionValuation(ForecastMaterialValuation Values,
    IReadOnlyDictionary<string, ForecastProductionEstimate> ByOrderKey);
public sealed record ForecastMaterialValue(double Benefit, double MaterialCost, double WorkCost)
{
    public double Net => Benefit - MaterialCost - WorkCost;
}

public static partial class PolicyFunctions
{
    // Missing estimates never fall back to batch count or zero. Known zero is an explicit assumption.
    private static ForecastMaterialValue? ValueMaterialPlan(IEnumerable<ForecastMaterialOption> options,
        ForecastMaterialValuation valuation, SortedSet<string> findings)
    {
        var relief = new Dictionary<string, double>(StringComparer.Ordinal);
        double materials = 0, work = 0, benefit = 0;
        bool Read(Fact<double> fact, string label, out double value)
        {
            if (!fact.TryGetValue(out value)) { findings.Add(label); return false; }
            if (!double.IsFinite(value) || value < 0) throw new DomainException("invalid production valuation: " + label);
            return true;
        }
        foreach (var option in options)
        {
            if (option.Estimate is not { } estimate) { findings.Add("unknown_estimate:" + option.Id); return null; }
            if (!Read(estimate.WorkTicks, "unknown_work:" + option.Id, out var ticks)) return null;
            if (ticks > 0)
            {
                if (!Read(valuation.WorkUtilityPerTick, "unknown_work_value", out var rate)) return null;
                work += ticks * rate;
            }
            foreach (var cost in option.Cost.Where(c => c.Count > 0))
            {
                if (!Read(valuation.MaterialUtilityPerUnit.GetValueOrDefault(cost.Resource), "unknown_material_value:" + cost.Resource.Value, out var rate)) return null;
                materials += cost.Count * rate;
            }
            foreach (var effect in estimate.Relief)
            {
                if (string.IsNullOrWhiteSpace(effect.Need)) throw new DomainException("empty production need");
                if (!Read(effect.Units, "unknown_relief:" + effect.Need, out var units)) return null;
                relief[effect.Need] = relief.GetValueOrDefault(effect.Need) + units;
            }
        }
        foreach (var pair in relief.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!double.IsFinite(pair.Value)) throw new DomainException("production relief overflow");
            if (pair.Value == 0) continue;
            if (!valuation.Needs.TryGetValue(pair.Key, out var need)) { findings.Add("unknown_need:" + pair.Key); return null; }
            if (!Read(need.Remaining, "unknown_need_remaining:" + pair.Key, out var remaining)
                || !Read(need.UtilityPerUnit, "unknown_need_value:" + pair.Key, out var unitValue)) return null;
            benefit += Math.Min(remaining, pair.Value) * unitValue;
        }
        var result = new ForecastMaterialValue(benefit, materials, work);
        if (!double.IsFinite(result.Net) || !double.IsFinite(benefit) || !double.IsFinite(materials) || !double.IsFinite(work))
            throw new DomainException("production valuation overflow");
        return result;
    }
}

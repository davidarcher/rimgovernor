namespace RimGovernor.Brain.Policy;

/// <summary>
/// Budgets a complete growth cycle plus stored-food reserve. Growing cells describe production capacity, never
/// food available for consumption. (Go <c>FieldCrop</c>.)
/// </summary>
public sealed record FieldCrop
{
    public Fact<bool> Edible { get; init; }

    public Fact<long> GrowingCells { get; init; }

    public Fact<double> Demand { get; init; }

    public Fact<double> GrowDays { get; init; }

    public Fact<double> HarvestNutrition { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>Go <c>FieldCoverage</c>.</summary>
    public static Fact<double> FieldCoverage(Fact<long> colonists, Fact<EquatableArray<FieldCrop>> crops, double reserveDays)
    {
        if (!crops.TryGetValue(out var rows) || !FieldPositive(reserveDays))
        {
            return Unknown<double>();
        }
        var coverage = 0.0;
        foreach (var crop in rows)
        {
            if (!crop.Edible.TryGetValue(out var edible))
            {
                return Unknown<double>();
            }
            if (!edible)
            {
                continue;
            }
            var ck = colonists.TryGetValue(out var count);
            var sk = crop.GrowingCells.TryGetValue(out var cells);
            var dk = crop.Demand.TryGetValue(out var demand);
            var gk = crop.GrowDays.TryGetValue(out var days);
            var yk = crop.HarvestNutrition.TryGetValue(out var yield);
            if (!ck || count < 0 || !sk || cells < 0 || !dk || !gk || !yk || !FieldPositive(demand) || !FieldPositive(days) || !FieldPositive(yield))
            {
                return Unknown<double>();
            }
            var target = FieldCoverageTarget(count, demand, days, yield, reserveDays);
            if (!FieldPositive(target))
            {
                return Unknown<double>();
            }
            coverage += cells / target;
        }
        if (double.IsInfinity(coverage) || double.IsNaN(coverage))
        {
            return Unknown<double>();
        }
        return Known(coverage);
    }

    internal static bool FieldPositive(double v) => v > 0 && !double.IsNaN(v) && !double.IsInfinity(v);

    private static double FieldCoverageTarget(long count, double demand, double days, double yield, double reserveDays) =>
        Math.Max(count * 10.0, Math.Ceiling(demand * ((days * 2.5) + reserveDays) / yield));

    /// <summary>
    /// Go <c>FieldCapacityTarget</c>: the cell count at which <see cref="FieldCoverage"/> reads 1 for the crop:
    /// growth stops there, so a shrink target below it would trim cells the planner keeps adding.
    /// </summary>
    public static Fact<int> FieldCapacityTarget(Fact<long> colonists, CropChoice crop, double reserve)
    {
        var ck = colonists.TryGetValue(out var count);
        var dk = crop.Demand.TryGetValue(out var demand);
        var gk = crop.GrowDays.TryGetValue(out var days);
        var yk = crop.HarvestNutrition.TryGetValue(out var yield);
        if (!ck || count < 0 || !dk || !gk || !yk || !FieldPositive(demand) || !FieldPositive(days) || !FieldPositive(yield) || !FieldPositive(reserve))
        {
            return Unknown<int>();
        }
        var target = FieldCoverageTarget(count, demand, days, yield, reserve);
        if (!FieldPositive(target) || target > 65536)
        {
            return Unknown<int>();
        }
        return Known((int)target);
    }
}

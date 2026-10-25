namespace RimGovernor.Brain.Policy;

/// <summary>Observed capacity of one planted food crop; growing cells are future production, not delivered food.</summary>
public sealed record FieldCrop
{
    public bool Edible { get; init; }
    public long GrowingCells { get; init; }
    public double Demand { get; init; }
    public double GrowDays { get; init; }
    public double HarvestNutrition { get; init; }
}

public static class FieldCapacityRules
{
    public static double FieldCoverage(long colonists, EquatableArray<FieldCrop> crops, double reserveDays)
    {
        if (colonists < 0 || !FieldPositive(reserveDays)) throw new RequiredFactException("field coverage budget");
        var coverage = 0.0;
        foreach (var crop in crops)
        {
            if (!crop.Edible || crop.Demand == 0) continue;
            if (crop.GrowingCells < 0 || !FieldPositive(crop.Demand) || !FieldPositive(crop.GrowDays) || !FieldPositive(crop.HarvestNutrition))
                throw new RequiredFactException("food field capacity");
            coverage += crop.GrowingCells / FieldCoverageTarget(colonists, crop.Demand, crop.GrowDays, crop.HarvestNutrition, reserveDays);
        }
        return double.IsFinite(coverage) ? coverage : throw new RequiredFactException("finite field coverage");
    }

    internal static bool FieldPositive(double v) => double.IsFinite(v) && v > 0;

    private static double FieldCoverageTarget(long count, double demand, double days, double yield, double reserveDays) =>
        Math.Max(count * 10.0, Math.Ceiling(demand * ((days * 2.5) + reserveDays) / yield));

    /// <summary>The cell count where coverage reaches one, shared by expansion and shrink planning.</summary>
    public static int FieldCapacityTarget(long colonists, CropChoice crop, double reserve)
    {
        if (colonists < 0 || !FieldPositive(crop.Demand) || !FieldPositive(crop.GrowDays) || !FieldPositive(crop.HarvestNutrition) || !FieldPositive(reserve))
            throw new RequiredFactException("field capacity budget");
        var target = FieldCoverageTarget(colonists, crop.Demand, crop.GrowDays, crop.HarvestNutrition, reserve);
        return FieldPositive(target) && target <= 65536 ? (int)target : throw new DomainException("field capacity exceeds supported cells");
    }
}

namespace RimGovernor.Brain.Policy;

public sealed record CropSeason
{
    public required RimGovernor.Contracts.Wire.Observations.FoodClimate Calendar { get; init; }
    public EquatableArray<DisasterCondition> Conditions { get; init; }
}

/// <summary>Whole visible zone exposure from the native zone census, independent of the planning window.</summary>
public abstract record FieldExposure
{
    private FieldExposure() { }
    public abstract long ZoneCells { get; }
    public abstract long UnroofedCells { get; }
    public abstract long Planted { get; }
    public abstract long Blighted { get; }

    public sealed record FromZone(RimGovernor.Contracts.Wire.Observations.ZoneState.Growing Observed) : FieldExposure
    {
        public override long ZoneCells => Observed.ZoneCells;
        public override long UnroofedCells => Observed.UnroofedCells;
        public override long Planted => Observed.PlantedCells;
        public override long Blighted => Observed.BlightedPlants;
    }

    /// <summary>A proposed open field has its planned cells and no planted or blighted crop.</summary>
    public sealed record PlannedOpen(long Cells) : FieldExposure
    {
        public override long ZoneCells => Cells;
        public override long UnroofedCells => Cells;
        public override long Planted => 0;
        public override long Blighted => 0;
    }
}
public static class CropRiskRules
{
    public static double CropPauseDays(EquatableArray<DisasterCondition> conditions)
    {
        var ticks = DisasterRules.ConditionRemainingTicks(conditions, DisasterRules.ConditionEclipse, DisasterRules.ConditionVolcanicWinter, DisasterRules.ConditionColdSnap);
        return ticks <= 0 ? 0 : (double)ticks / GameTime.TicksPerDay;
    }

    internal static bool ToxicFalloutActive(EquatableArray<DisasterCondition> conditions) =>
        conditions.Any(c => c.Definition == DisasterRules.ConditionToxicFallout);

    internal static double Share(long part, long whole)
    {
        if (whole < 0 || part < 0 || part > whole) throw new RequiredFactException("field exposure counts");
        return whole == 0 ? 0 : (double)part / whole;
    }

    public static double FalloutRisk(EquatableArray<DisasterCondition> conditions, FieldExposure e) =>
        ToxicFalloutActive(conditions) ? Share(e.UnroofedCells, e.ZoneCells) : 0;

    public static double BlightRisk(FieldExposure e) => Share(e.Blighted, e.Planted);

    public static double FrostRisk(double lead, RimGovernor.Contracts.Wire.Observations.FoodClimate calendar, FieldExposure e)
    {
        if (!calendar.Valid() || !double.IsFinite(lead) || lead < 0) throw new RequiredFactException("field harvest timing");
        return lead <= calendar.GrowingDaysRemaining ? 0 : Share(e.UnroofedCells, e.ZoneCells) * Math.Min(1, calendar.NonGrowingDays / lead);
    }

    internal static List<CandidateRisk> CropRisks(CropSeason season, double lead, FieldExposure e)
    {
        var result = new List<CandidateRisk>();
        (CandidateRiskKind Kind, double Weight)[] risks =
        [
            (CandidateRiskKind.Fallout, FalloutRisk(season.Conditions, e)),
            (CandidateRiskKind.Blight, BlightRisk(e)),
            (CandidateRiskKind.Frost, FrostRisk(lead, season.Calendar, e)),
        ];
        foreach (var (kind, weight) in risks)
            if (weight > 0) result.Add(new CandidateRisk(kind, Math.Min(1, weight)));
        return result;
    }

    internal static double WithCropPause(double lead, EquatableArray<DisasterCondition> conditions) =>
        double.IsFinite(lead) && lead >= 0 ? Math.Min(CalendarRules.YearDays, lead + CropPauseDays(conditions)) : throw new RequiredFactException("crop harvest lead");
}

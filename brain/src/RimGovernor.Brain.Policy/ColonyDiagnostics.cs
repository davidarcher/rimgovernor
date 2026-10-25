namespace RimGovernor.Brain.Policy;

/// <summary>The facts one projection reads. Observed diagnostics are separate from proposed inventory changes.</summary>
public sealed record ColonyForecastFacts
{
    private RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot? census;
    public RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot Census { get => census ?? throw new RequiredFactException("colony census"); init => census = value; }

    public FoodSupply Food { get; init; } = new() { Larder = new() { RawMeatNutrition = 0, CookDemandNutrition = 0, Corpses = [] } };

    /// <summary>The frame read no food census: Food stays empty and every food result of the projection is unknown.</summary>
    public bool FoodUnread { get; init; }
    public MedicalReserveObservation Medicine { get; init; } = new();

    public ItemFacts Items { get; init; } = new();
}

public static class ColonyDiagnosticsRules
{
    /// <summary>Existing maintenance target window; independent of the requested plan horizon.</summary>
    public const double MaintenanceHorizonDays = 5.0;

    /// <summary>The days a runway falls short of target, as NutritionDemand reads it.</summary>
    public static double RunwayShortfall(double runway, double target) => Math.Max(0, target - runway);
}

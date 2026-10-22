namespace RimGovernor.Brain.Policy;

/// <summary>The facts one projection reads. Observed diagnostics are separate from proposed inventory changes.</summary>
public sealed record ColonyForecastFacts
{
    /// <summary>Complete observed resource census. Known empty means zero stock, not unavailable.</summary>
    public Fact<EquatableArray<Amount>> Stock { get; init; }
    public Fact<EquatableArray<string>> ResourceThings { get; init; }

    public FoodSupply Food { get; init; } = new();
    public MedicalReserveObservation Medicine { get; init; } = new();

    public IReadOnlyList<PowerNetworkFact> Power { get; init; } = [];

    public ConstructionInputs Construction { get; init; } = new();

    public ItemFacts Items { get; init; } = new();
}

/// <summary>
/// Describes current conditions against the maintenance horizon. These are not plan trajectories. An unread input is an unknown
/// fact, never a default.
/// </summary>
public sealed record ColonyDiagnostics
{
    public double HorizonDays { get; init; }

    public Fact<PowerProjection> Power { get; init; }

    public Fact<ConstructionProjection> Construction { get; init; }
}

/// <summary>One net's stored energy over its current net drain.</summary>
public sealed record PowerNetProjection(string Id, double ReserveDays, double ShortfallDays);

public sealed record PowerProjection
{
    public IReadOnlyList<PowerNetProjection> Nets { get; init; } = [];

    /// <summary>The largest per-net shortfall.</summary>
    public double ShortfallDays { get; init; }
}

public static class ColonyDiagnosticsRules
{
    /// <summary>Existing maintenance target window; independent of the requested plan horizon.</summary>
    public const double MaintenanceHorizonDays = 5.0;

    /// <summary>Retains current-condition diagnostics without treating planned effects as observations.</summary>
    internal static ColonyDiagnostics DiagnoseColony(ColonyForecastFacts input) =>
        new()
        {
            HorizonDays = MaintenanceHorizonDays,
            Power = ProjectPower(input.Power),
            Construction = ConstructionProjectionRules.ProjectConstruction(input.Construction, input.Stock),
        };

    /// <summary>The days a runway falls short of target: the one formula the power projection and NutritionDemand share.</summary>
    public static double RunwayShortfall(double runway, double target) => Math.Max(0, target - runway);

    private static Fact<PowerProjection> ProjectPower(IReadOnlyList<PowerNetworkFact> nets)
    {
        if (nets.Count == 0)
        {
            return Unknown<PowerProjection>();
        }
        var rows = new List<PowerNetProjection>();
        var shortfall = 0.0;
        foreach (var n in nets)
        {
            var days = n.ReserveDays();
            var row = new PowerNetProjection(n.Id, days, RunwayShortfall(days, MaintenanceHorizonDays));
            shortfall = Math.Max(shortfall, row.ShortfallDays);
            rows.Add(row);
        }
        return Known(new PowerProjection { Nets = [.. rows.OrderBy(n => n.Id, StringComparer.Ordinal)], ShortfallDays = shortfall });
    }
}

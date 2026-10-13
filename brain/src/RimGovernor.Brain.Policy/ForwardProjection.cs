namespace RimGovernor.Brain.Policy;

/// <summary>The facts one projection reads. Shadow only: nothing in admission consumes the result. (Go <c>ForwardInputs</c>.)</summary>
public sealed record ForwardInputs
{
    public FoodSupply Food { get; init; } = new();

    public IReadOnlyList<PowerNetworkFact> Power { get; init; } = [];

    public SleepingRange Sleeping { get; init; } = new();

    public Fact<IReadOnlyList<DisasterCondition>> Conditions { get; init; }

    public Fact<IReadOnlyList<DefenseTurretFacts>> Turrets { get; init; }

    public RoundsPolicy Policy { get; init; } = new();

    public ConstructionInputs Construction { get; init; } = new();

    public FuelInputs Fuel { get; init; } = new();

    /// <summary>The resource runways the drug, material and medicine domains read, split by the catalog's Items.</summary>
    public IReadOnlyList<ResourceRunway> Runways { get; init; } = [];

    public ItemFacts Items { get; init; } = new();

    public AnimalFeedInputs AnimalFeed { get; init; } = new();
}

/// <summary>The coldest and hottest observed sleeping-room temperature. (Go <c>SleepingRange</c>.)</summary>
public sealed record SleepingRange
{
    public Fact<double> Min { get; init; }

    public Fact<double> Max { get; init; }
}

/// <summary>
/// Rolls each resource forward over HorizonDays. A resource whose inputs are not all observed is an unknown
/// fact, never a default. (Go <c>ForwardProjection</c>.)
/// </summary>
public sealed record ForwardProjection
{
    public double HorizonDays { get; init; }

    public Fact<FoodProjection> Food { get; init; }

    public Fact<PowerProjection> Power { get; init; }

    public Fact<TemperatureProjection> Temperature { get; init; }

    public Fact<DefenseProjection> Defense { get; init; }

    public Fact<ConstructionProjection> Construction { get; init; }

    public Fact<FuelProjection> Fuel { get; init; }

    public Fact<StockProjection> Drugs { get; init; }

    public Fact<StockProjection> Resources { get; init; }

    public Fact<StockProjection> Medicine { get; init; }

    public Fact<AnimalFeedProjection> AnimalFeed { get; init; }
}

public sealed record FoodProjection(double RunwayDays, double ShortfallDays);

/// <summary>One net's stored energy over its current net drain. (Go <c>PowerNetProjection</c>.)</summary>
public sealed record PowerNetProjection(string Id, double ReserveDays, double ShortfallDays);

public sealed record PowerProjection
{
    public IReadOnlyList<PowerNetProjection> Nets { get; init; } = [];

    /// <summary>The largest per-net shortfall.</summary>
    public double ShortfallDays { get; init; }
}

/// <summary>
/// Holds the observed sleeping-room extremes: the native read carries no rate of change, so a breach observed
/// now is projected to last the whole horizon. ExtremeConditionDays is the observed remaining ColdSnap/HeatWave
/// duration inside the horizon, for context. (Go <c>TemperatureProjection</c>.)
/// </summary>
public sealed record TemperatureProjection(double MinC, double MaxC, double BreachDays, double ExtremeConditionDays);

/// <summary>
/// Reports only what is observed. Ammunition or fuel burn and raid arrival have no native rate, so they are
/// listed in Gaps, not guessed. Dps is summed over turrets that are powered and not out of fuel. (Go <c>DefenseProjection</c>.)
/// </summary>
public sealed record DefenseProjection(int Turrets, double Dps, int DisabledCount, IReadOnlyList<string> Gaps);

public static partial class PolicyFunctions
{
    /// <summary>Bounds the shared forecast.</summary>
    public const double ProjectionHorizonDays = 5.0;

    /// <summary>Go <c>ProjectForward</c>: projects food, power, temperature and defense over <see cref="ProjectionHorizonDays"/>.</summary>
    public static ForwardProjection ProjectForward(ForwardInputs input) =>
        new()
        {
            HorizonDays = ProjectionHorizonDays,
            Food = ProjectFood(input.Food),
            Power = ProjectPower(input.Power),
            Temperature = ProjectTemperature(input),
            Defense = ProjectDefense(input.Turrets),
            Construction = ProjectConstruction(input.Construction),
            Fuel = PlanFuelRunway(input.Fuel).Projection,
            Drugs = PlanDrugRunway(input.Runways, input.Items),
            Resources = PlanMaterialRunway(input.Runways, input.Items),
            Medicine = PlanMedicineRunway(input.Runways, input.Items),
            AnimalFeed = PlanAnimalFeedRunway(input.AnimalFeed).Projection,
        };

    /// <summary>Go <c>RunwayShortfall</c>: the days a runway falls short of target: the one formula the food projection, the power projection and NutritionDemand share.</summary>
    public static double RunwayShortfall(double runway, double target) => Math.Max(0, target - runway);

    private static Fact<FoodProjection> ProjectFood(FoodSupply supply)
    {
        FoodForecast forecast;
        try
        {
            forecast = ForecastFood(supply, null);
        }
        catch (DomainException)
        {
            return Unknown<FoodProjection>();
        }
        if (!forecast.RunwayDays.TryGetValue(out var days) || !Finite(days))
        {
            return Unknown<FoodProjection>();
        }
        return Known(new FoodProjection(days, RunwayShortfall(days, ProjectionHorizonDays)));
    }

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
            if (!n.ReserveDays().TryGetValue(out var days) || double.IsNaN(days) || days < 0)
            {
                return Unknown<PowerProjection>();
            }
            var row = new PowerNetProjection(n.Id, days, RunwayShortfall(days, ProjectionHorizonDays));
            shortfall = Math.Max(shortfall, row.ShortfallDays);
            rows.Add(row);
        }
        return Known(new PowerProjection { Nets = [.. rows.OrderBy(n => n.Id, StringComparer.Ordinal)], ShortfallDays = shortfall });
    }

    private static Fact<TemperatureProjection> ProjectTemperature(ForwardInputs input)
    {
        var lk = input.Sleeping.Min.TryGetValue(out var lo);
        var hk = input.Sleeping.Max.TryGetValue(out var hi);
        if (!lk || !hk || !Finite(lo) || !Finite(hi) || lo > hi)
        {
            return Unknown<TemperatureProjection>();
        }
        if (!ConditionRemainingTicks(input.Conditions, ConditionKind.ColdSnap, ConditionKind.HeatWave).TryGetValue(out var ticks))
        {
            return Unknown<TemperatureProjection>();
        }
        var breach = lo < input.Policy.ColdEnter || hi > input.Policy.HotEnter ? ProjectionHorizonDays : 0;
        return Known(new TemperatureProjection(lo, hi, breach, Math.Min(ProjectionHorizonDays, (double)ticks / GameTime.TicksPerDay)));
    }

    private static Fact<DefenseProjection> ProjectDefense(Fact<IReadOnlyList<DefenseTurretFacts>> turrets)
    {
        if (!turrets.TryGetValue(out var rows))
        {
            return Unknown<DefenseProjection>();
        }
        var dpsTotal = 0.0;
        var disabled = 0;
        foreach (var t in rows)
        {
            var pk = t.Powered.TryGetValue(out var powered);
            var ek = t.OutOfFuel.TryGetValue(out var empty);
            var dk = t.Dps.TryGetValue(out var dps);
            if (!pk || !ek || !dk || !Finite(dps) || dps < 0)
            {
                return Unknown<DefenseProjection>();
            }
            if (powered && !empty)
            {
                dpsTotal += dps;
            }
            else
            {
                disabled++;
            }
        }
        return Known(new DefenseProjection(rows.Count, dpsTotal, disabled, ["turret ammunition or fuel burn rate", "raid arrival"]));
    }
}

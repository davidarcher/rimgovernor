namespace RimGovernor.Brain.Policy;

/// <summary>
/// Additional observed diagnostic inputs collected with the routine frame.
/// </summary>
public sealed record ColonyForecastObserved
{
    public RimGovernor.Contracts.Wire.Observations.NeedsForecastRead? Needs { get; init; }
    public Fact<EquatableArray<WorkPawn>> WorkPawns { get; init; }
    public IReadOnlyList<PowerNetworkFact> Power { get; init; } = [];

    public Fact<EquatableArray<DefenseTurretFacts>> Turrets { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>Assembles observed facts, never proposed stock or duplicated resource targets.</summary>
    public static ColonyForecastFacts ColonyForecastFactsOf(RoundsFacts f, RoundsPolicy p)
    {
        var input = new ColonyForecastFacts
        {
            Stock = f.Resources,
            Power = f.ForecastObserved.Power,
            Sleeping = new SleepingRange { Min = f.SleepingMin, Max = f.SleepingMax },
            Conditions = f.DisasterConditions,
            Turrets = f.ForecastObserved.Turrets,
            Policy = p,
            Construction = new ConstructionInputs { Deficit = f.ConstructionDeficit, Admitted = f.Admitted },
            Fuel = new FuelInputs { Consumers = f.Fuel, Stock = new StockReader { Resources = f.Resources, Wood = f.Wood } },
            AnimalFeed = AnimalFeedRunwayExtensions.AnimalFeedInputsOf(f),
            Runways = f.ResourceRunways,
            Items = f.Items,
        };
        return f.AnimalUpkeep.Food.TryGetValue(out var supply) ? input with { Food = supply } : input;
    }
}

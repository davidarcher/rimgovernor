namespace RimGovernor.Brain.Policy;

/// <summary>
/// The projector inputs the routine facts do not already carry (food, sleeping range and conditions are read
/// from the facts). (Go <c>ForwardObserved</c>.)
/// </summary>
public sealed record ForwardObserved
{
    public IReadOnlyList<PowerNetworkFact> Power { get; init; } = [];

    public Fact<IReadOnlyList<DefenseTurretFacts>> Turrets { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>Go <c>ForwardInputsOf</c>: assembles the projector inputs from the routine facts.</summary>
    public static ForwardInputs ForwardInputsOf(RoundsFacts f, RoundsPolicy p)
    {
        var input = new ForwardInputs
        {
            Power = f.Forward.Power,
            Sleeping = new SleepingRange { Min = f.SleepingMin, Max = f.SleepingMax },
            Conditions = f.DisasterConditions,
            Turrets = f.Forward.Turrets,
            Policy = p,
            Construction = new ConstructionInputs { Deficit = f.ConstructionDeficit, Admitted = f.Admitted, Stock = f.Resources, Items = f.Items },
            Fuel = new FuelInputs { Consumers = f.Fuel, Stock = new StockReader { Resources = f.Resources, Wood = f.Wood } },
            AnimalFeed = f.AnimalFeedInputs(),
            Runways = f.ResourceRunways,
            Items = f.Items,
        };
        return f.AnimalUpkeep.Food.TryGetValue(out var supply) ? input with { Food = supply } : input;
    }
}

namespace RimGovernor.Brain.Policy;

/// <summary>
/// Additional observed diagnostic inputs collected with the routine frame.
/// </summary>
public sealed record ColonyForecastObserved
{
    public RimGovernor.Contracts.Wire.Observations.NeedsForecastRead? Needs { get; init; }
    public RimGovernor.Contracts.Wire.Observations.CoupledForecastRead? Coupled { get; init; }
    public Fact<EquatableArray<WorkPawn>> WorkPawns { get; init; }
    public IReadOnlyList<PowerNetworkFact> Power { get; init; } = [];
}

public static class ColonyForecastFactsRules
{
    /// <summary>Assembles observed facts, never proposed stock or duplicated resource targets.</summary>
    public static ColonyForecastFacts ColonyForecastFactsOf(RoundsFacts f)
    {
        var input = new ColonyForecastFacts
        {
            Stock = f.Resources,
            ResourceThings = f.ResourceThings,
            Power = f.ForecastObserved.Power,
            Construction = new ConstructionInputs { Deficit = f.CurrentConstruction?.Deficit, Admitted = f.Admitted },
            Items = f.Items,
            Medicine = f.MedicalReserve,
        };
        return f.AnimalUpkeep.Food.TryGetValue(out var supply) ? input with { Food = supply } : input with { FoodUnread = true };
    }
}

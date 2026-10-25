namespace RimGovernor.Brain.Policy;

/// <summary>
/// Additional observed diagnostic inputs collected with the routine frame.
/// </summary>
public sealed record ColonyForecastObserved
{
    public RimGovernor.Contracts.Wire.Observations.NeedsForecastRead? Needs { get; init; }
    public RimGovernor.Contracts.Wire.Observations.CoupledForecastRead? Coupled { get; init; }
    public WorkPawnsRead WorkPawns { get; init; } = new WorkPawnsRead([]);
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
            Items = f.Items,
            Medicine = f.MedicalReserve,
        };
        return f.AnimalUpkeep.Food.TryGetValue(out var supply) ? input with { Food = supply } : input with { FoodUnread = true };
    }
}

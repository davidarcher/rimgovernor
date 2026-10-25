namespace RimGovernor.Brain.Policy;

/// <summary>
/// Additional observed diagnostic inputs collected with the routine frame.
/// </summary>
public sealed record ColonyForecastObserved
{
    public RimGovernor.Brain.GameFacts.Observations.NeedsForecastRead? Needs { get; init; }
    public RimGovernor.Brain.GameFacts.Observations.CoupledForecastRead? Coupled { get; init; }
    public WorkPawnsRead WorkPawns { get; init; } = new WorkPawnsRead([]);
}

public static class ColonyForecastFactsRules
{
    /// <summary>Assembles observed facts, never proposed stock or duplicated resource targets.</summary>
    public static ColonyForecastFacts ColonyForecastFactsOf(ReviewFacts f)
    {
        var input = new ColonyForecastFacts
        {
            Census = f.Census,
            Items = f.Items,
            Medicine = f.MedicalReserve,
        };
        return input with { Food = f.AnimalUpkeep.Food };
    }
}

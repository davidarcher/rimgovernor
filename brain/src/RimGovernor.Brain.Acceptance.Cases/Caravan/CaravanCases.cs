namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The caravan acceptance cases; <see cref="Register"/> registers them.</summary>
public static class CaravanCases
{
    /// <summary>Registers every <c>caravan/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(CaravanDeparture.Case());
    }
}

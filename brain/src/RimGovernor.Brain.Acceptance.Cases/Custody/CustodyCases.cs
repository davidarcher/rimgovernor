namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>custody/*</c> acceptance cases; <see cref="Register"/> registers them.</summary>
public static class CustodyCases
{
    /// <summary>Registers every <c>custody/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(CustodyPopulation.Case());
    }
}

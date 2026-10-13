namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>custody/*</c> acceptance cases (Go <c>cases/custody</c>: the population custody dispatch cases); <see cref="Register"/> replaces the Go package's <c>init()</c>.</summary>
public static class CustodyCases
{
    /// <summary>Registers every <c>custody/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(CustodyPopulation.Case());
    }
}

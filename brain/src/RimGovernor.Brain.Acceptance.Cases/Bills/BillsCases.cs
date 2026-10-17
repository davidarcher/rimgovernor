namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>bills/*</c> acceptance cases; <see cref="Register"/> registers them.</summary>
public static class BillsCases
{
    /// <summary>Registers every <c>bills/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(BillsCensus.Case());
    }
}

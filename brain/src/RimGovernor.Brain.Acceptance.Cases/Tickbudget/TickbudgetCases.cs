namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>tickbudget/*</c> cases (Go <c>cases/tickbudget</c>).</summary>
public static class TickbudgetCases
{
    /// <summary>Registers every <c>tickbudget/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(TickbudgetBoundaries.Case());
    }
}

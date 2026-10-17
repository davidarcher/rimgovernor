namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>Registers the <c>defense</c> area's cases (Go: <c>cases/defense</c>'s <c>init()</c>s).</summary>
public static class DefenseCases
{
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(DefenseSiteStuff.Case());
        registry.Register(DefenseThreat.Case());
    }
}

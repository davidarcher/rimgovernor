namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>explain/*</c> acceptance cases (Go <c>cases/explain</c>); <see cref="Register"/> replaces the Go package's <c>init()</c>.</summary>
public static class ExplainCases
{
    /// <summary>Registers every <c>explain/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(ExplainEveryConcern.Case());
    }
}

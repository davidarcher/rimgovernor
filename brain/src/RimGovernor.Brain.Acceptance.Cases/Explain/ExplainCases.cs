namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>explain/*</c> acceptance cases; <see cref="Register"/> registers them.</summary>
public static class ExplainCases
{
    /// <summary>Registers every <c>explain/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(ExplainEveryConcern.Case());
    }
}

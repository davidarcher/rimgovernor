namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>stats/*</c> cases (Go <c>cases/stats</c>); <see cref="Register"/> replaces the Go package's <c>init()</c>.</summary>
public static class StatsCases
{
    /// <summary>Registers every <c>stats/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(StatsEvaluateStat.Case());
    }
}

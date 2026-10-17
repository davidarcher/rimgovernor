namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>stats/*</c> cases; <see cref="Register"/> registers them.</summary>
public static class StatsCases
{
    /// <summary>Registers every <c>stats/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(StatsEvaluateStat.Case());
    }
}

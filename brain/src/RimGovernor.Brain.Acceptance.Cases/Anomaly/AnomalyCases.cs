namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>anomaly/*</c> acceptance cases; <see cref="Register"/> registers them.</summary>
public static class AnomalyCases
{
    /// <summary>Registers every <c>anomaly/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(AnomalyHorror.Case());
    }
}

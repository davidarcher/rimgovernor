namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>anomaly/*</c> acceptance cases (Go <c>cases/anomaly</c>); <see cref="Register"/> replaces the Go package's <c>init()</c>.</summary>
public static class AnomalyCases
{
    /// <summary>Registers every <c>anomaly/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(AnomalyHoldStudy.Case());
        registry.Register(AnomalyHorror.Case());
        registry.Register(AnomalyMonolithDisrupt.Case());
    }
}

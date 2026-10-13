namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The speedmatrix case family (Go <c>cases/speedmatrix</c>).</summary>
public static class SpeedmatrixCases
{
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(SpeedmatrixRun.PlainCase());
        registry.Register(SpeedmatrixRun.ObservationsCase());
    }
}

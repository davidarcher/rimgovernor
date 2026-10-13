namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>startup/*</c> cases (Go <c>cases/startup</c>).</summary>
public static class StartupCases
{
    /// <summary>Registers every <c>startup/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(StartupLaborCase.Case());
    }
}

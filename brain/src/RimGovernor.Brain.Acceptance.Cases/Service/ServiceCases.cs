namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The cases about the controller process itself (Go <c>cases/service</c>); <see cref="Register"/> replaces the Go package's <c>init()</c>.</summary>
public static class ServiceCases
{
    /// <summary>Registers every <c>service/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(ServiceRestart.Case());
    }
}

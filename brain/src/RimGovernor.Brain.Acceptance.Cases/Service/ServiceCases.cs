namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The cases about the controller process itself; <see cref="Register"/> registers them.</summary>
public static class ServiceCases
{
    /// <summary>Registers every <c>service/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(ServiceRestart.Case());
    }
}

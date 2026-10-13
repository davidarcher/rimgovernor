namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>temperature/*</c> acceptance cases (Go <c>cases/temperature</c>).</summary>
public static class TemperatureCases
{
    /// <summary>Registers every <c>temperature/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(BuildingTemperature.Case());
    }
}

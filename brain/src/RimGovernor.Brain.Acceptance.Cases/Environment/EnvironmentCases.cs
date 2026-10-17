namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>environment/*</c> acceptance cases: native probes of the ambient-temperature mood mechanics.</summary>
public static class EnvironmentCases
{
    /// <summary>Registers every <c>environment/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(TemperatureProbe.Case(hot: false));
        registry.Register(TemperatureProbe.Case(hot: true));
    }
}

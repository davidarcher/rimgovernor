namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>power/*</c> acceptance cases (Go <c>cases/power</c>): the power/basic Need vertical.</summary>
public static class PowerCases
{
    /// <summary>Registers every <c>power/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(PowerFuel.Case());
        registry.Register(PowerWindThinRoof.Case());
    }
}

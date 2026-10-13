namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>light/*</c> acceptance cases (Go <c>cases/light</c>): the MaintainLighting vertical.</summary>
public static class LightCases
{
    /// <summary>Registers every <c>light/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(LightDark.Case());
    }
}

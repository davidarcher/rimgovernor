namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>light/*</c> acceptance cases: the rooms/lighting Need vertical.</summary>
public static class LightCases
{
    /// <summary>Registers every <c>light/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(LightDark.Case());
    }
}

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The fixture generators and census tools; <see cref="Register"/> registers them.</summary>
public static class ToolsCases
{
    /// <summary>Registers every <c>tools/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(ToolsDeepResources.Case());
        registry.Register(ToolsFoodChannels.Case());
    }
}

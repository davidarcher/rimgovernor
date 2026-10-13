namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The fixture generators and census tools (Go <c>cases/tools</c>); <see cref="Register"/> replaces the Go package's <c>init()</c>.</summary>
public static class ToolsCases
{
    /// <summary>Registers every <c>tools/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(ToolsBaselineGen.Case());
        registry.Register(ToolsDeepResources.Case());
        registry.Register(ToolsFoodChannels.Case());
        foreach (var c in ToolsVariantSaveGen.Cases())
        {
            registry.Register(c);
        }
    }
}

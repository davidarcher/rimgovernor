namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>redevelopment/*</c> cases; <see cref="Register"/> adds them to the catalog.</summary>
public static class RedevelopmentCases
{
    /// <summary>Registers every <c>redevelopment/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(ReplaceBeforeRemove.Case());
    }
}

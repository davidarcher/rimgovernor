namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>clearance/*</c> cases; <see cref="Register"/> adds them to the catalog.</summary>
public static class ClearanceCases
{
    /// <summary>Registers every <c>clearance/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(ClearancePackReinstall.Case());
        registry.Register(ClearanceForeignIds.Case());
        registry.Register(ClearanceRetiredGround.Case());
    }
}

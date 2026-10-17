namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The <c>wall/*</c> acceptance cases: deconstruction, clearance, wall upgrade and guarded construction behaviours that
/// live in the native handlers. <see cref="Register"/> adds them to the catalog.
/// </summary>
public static class WallCases
{
    /// <summary>Registers every <c>wall/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(WallClearedRoom.Case());
        registry.Register(WallConstructionSkill.Case());
        registry.Register(WallDeconstruct.Case());
        registry.Register(WallDoorSwap.Case());
        registry.Register(WallLayers.Case());
        registry.Register(WallRemotePickup.Case());
        registry.Register(WallRemoval.Case());
        registry.Register(WallReplace.Case());
        registry.Register(WallTierGate.Case());
        registry.Register(WallUpgrade.Case());
    }
}

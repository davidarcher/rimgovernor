namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The <c>layout/*</c> acceptance cases: native geometry the layout planner relies on.
/// </summary>
public static class LayoutCases
{
    /// <summary>Registers every <c>layout/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(LayoutPowerSites.Case());
        registry.Register(LayoutFirstPlanHeld.Case());
        registry.Register(LayoutFirstRoomTemperate.Case());
        registry.Register(LayoutFirstRoomIce.Case());
        registry.Register(LayoutBarracksSplits.Case());
    }
}

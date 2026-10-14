namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>The first priority class that waits on the initial shelter's builders: preempt (0) and critical (1) work never does.</summary>
    internal const int ConstructionLaborWaitsFromPriority = 2;

    /// <summary>
    /// Reports whether an action's execution is Construction work, the labor the initial shelter's builders
    /// spend: raising or tearing down a structure, roofing, clearing cover. Digging is Mining work, hauling and
    /// growing are their own work, so those never compete for it.
    /// </summary>
    public static bool DrawsConstructionLabor(ActionKind kind) => kind is
        ActionKind.Building or ActionKind.BuildRoof or ActionKind.RemoveRoof or ActionKind.Deconstruction
        or ActionKind.FloorRemoval or ActionKind.FoundationRemoval or ActionKind.WallRemoval
        or ActionKind.MoveBuilding or ActionKind.UninstallBuilding or ActionKind.CoverClearance;

    /// <summary>
    /// Reports whether a proposal waits for the initial shelter's builders. While the housing latch is at the
    /// shelter phase the shelter owns the Construction labor: a plan holding an action that draws on it is refused
    /// unless it serves the housing goal itself, an emergency, or work of the preempt and critical classes. A plan
    /// of other kinds (gathering, growing, zoning, bills, settings) never waits, so the supply that the shelter's
    /// own demand asks for is not held behind it.
    /// </summary>
    public static bool ConstructionLaborHeld(RoundsLatches latches, EquatableArray<ConcernId> emergency, ConcernId need, int priority, IEnumerable<ActionKind> kinds)
    {
        if (latches.Housing != Phase.HousingShelter || need == MaintainHousing || priority < ConstructionLaborWaitsFromPriority || emergency.Contains(need))
        {
            return false;
        }
        return kinds.Any(DrawsConstructionLabor);
    }
}

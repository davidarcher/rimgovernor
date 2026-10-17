namespace RimGovernor.Brain.Policy;

public static class ConstructionLaborRules
{
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
    /// Reports whether a proposal waits for the initial shelter's builders. While the review finds the starter
    /// shelter owed (<see cref="RoundsLatches.Housing"/>) the shelter owns the Construction labor: a plan holding an
    /// action that draws on it is refused unless it serves a shelter housing Need itself (<paramref name="need"/> is
    /// the owner's Concern, or a Need owner's filing key). A plan
    /// of other kinds (gathering, growing, zoning, bills, settings) never waits, so the supply that the shelter's
    /// own demand asks for is not held behind it.
    /// </summary>
    public static bool ConstructionLaborHeld(RoundsLatches latches, ConcernId need, IEnumerable<ActionKind> kinds)
    {
        if (latches.Housing != Phase.HousingShelter || ShelterFamily.Housing(need))
        {
            return false;
        }
        return kinds.Any(DrawsConstructionLabor);
    }
}

using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

// Staging the morgue. The planned morgue stands in the outskirts cluster from the start of the plan and is
// shelled once a human corpse, colonist or stranger, fresh or rotten, lies waiting. Cooling never gates the
// shell: a walled-in room already keeps corpses out of sight, and cold only slows rot (MaintainRefrigeration
// cools it like any standing cooling room, WarmRooms). Vanilla haulers carry the corpses; nothing here hauls.
public static partial class PolicyFunctions
{
    /// <summary>Reports a human corpse lying unburied.</summary>
    public static bool MorgueWaiting(IReadOnlyList<WasteItem> waste) =>
        waste.Any(item => item.State != WasteState.Buried && item.CorpseOf is CorpseOf.Stranger or CorpseOf.Colonist);

    /// <summary>
    /// The planned morgue whose ring does not match the ground while a human corpse waits; null otherwise.
    /// The morgue holds no furniture: the build side reconciles its ring and floor (ReconcileRoom).
    /// </summary>
    public static PlannedRoom? MorgueRoomOwed(LayoutPlan plan, GroundCensus ground, IReadOnlyList<WasteItem> waste)
    {
        if (!MorgueWaiting(waste))
        {
            return null;
        }
        foreach (var r in plan.Rooms)
        {
            if (r.Role != PlannedRole.Morgue)
            {
                continue;
            }
            return plan.GroundMatches(r, ground) ? null : r;
        }
        return null;
    }
}

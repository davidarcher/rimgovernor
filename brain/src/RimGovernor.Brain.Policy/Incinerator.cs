namespace RimGovernor.Brain.Policy;

// The incinerator: a walled, unroofed 3x3 room inside the waste yard, planned from the start in the
// outskirts cluster (the outskirts nodes of the station graph). Burnable waste hauls into it from the dump
// (the Sanitation store of IncinerationOwner, a higher priority zone over its floor) and it is burned whole.
// Its room is a plan room like the tomb's: the walls and door are non-flammable (observation
// FireproofStuff) so the fire stays inside, and the room has no roof so it does not heat up and kill the
// pawns who clean the ash. Unlike every other room it is permanent: clothing always wears and corpses
// always happen, so the plan never drops or moves it.
//
// Go's PlannedIncinerator (PlannedRole "incinerator") is the member PlannedRole.Incinerator, declared with
// the PlannedRole enum.
public static partial class PolicyFunctions
{
    /// <summary>The plan's incinerator rooms, in plan order.</summary>
    public static IReadOnlyList<PlannedRoom> IncineratorRooms(this LayoutPlan p) => p.RoomsOf(PlannedRole.Incinerator);
}

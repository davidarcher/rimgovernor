package policy

// The incinerator: a walled, unroofed 3x3 room inside the
// waste yard, planned from the start in the outskirts cluster
// (growOutskirtsRooms). Burnable waste hauls into it from the dump (the Sanitation store
// of incinerationOwner, a higher priority zone over its floor) and it is
// burned whole. Its room is a plan room like the tomb's: the walls and door are
// non-flammable (observation FireproofStuff) so the fire stays inside, and the
// room has no roof so it does not heat up and kill the pawns who clean the ash.
// Unlike every other room it is permanent: clothing always wears and corpses
// always happen, so the plan never drops or moves it.

// PlannedIncinerator is the incinerator's plan role.
const PlannedIncinerator PlannedRole = "incinerator"

// IncineratorRooms are the plan's incinerator rooms, in plan order.
func (p LayoutPlan) IncineratorRooms() []PlannedRoom { return p.roomsOf(PlannedIncinerator) }

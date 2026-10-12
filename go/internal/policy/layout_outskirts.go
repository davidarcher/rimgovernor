package policy

// The outskirts rooms (morgue, tomb, graveyard, waste yard, incinerator) are
// ordinary graph nodes the tiler sites; their interior sizes are below.

const (
	// GraveyardW and GraveyardH are the graveyard's interior.
	GraveyardW int32 = 11
	GraveyardH int32 = 7
	// WasteYardW and WasteYardH are the waste yard's interior: 77
	// cells, the incinerator room takes a corner, the dump the rest.
	WasteYardW int32 = 11
	WasteYardH int32 = 7
	// IncineratorOutline is the incinerator room's walled outline side: a 3x3
	// interior and its ring.
	IncineratorOutline int32 = 5
)

// PlannedWasteYard is the waste yard's plan role: an Outdoor room that
// holds the dump zone's ground and the incinerator room.
const PlannedWasteYard PlannedRole = "waste_yard"

// WasteYardRooms are the plan's waste yards.
func (p LayoutPlan) WasteYardRooms() []PlannedRoom { return p.roomsOf(PlannedWasteYard) }

package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// UpkeepRoom is one row of the upkeep room census (#810): a room holding a
// colonist bed or carrying a common role (DiningRoom, RecRoom), with its
// native quality stats (unknown when the read omitted any of them). Beds
// lists the colonist beds (humanlike, not medical, not for prisoners)
// standing in the room; a bed appears in at most one room.
type UpkeepRoom struct {
	ID string
	// Role is the native RoomRoleDef name, empty when the room has none.
	Role    string
	Quality domain.Fact[RoomQuality]
	Cells   domain.Fact[int]
	Beds    []string
}

// RoyalTitle is a colonist's most senior royal title and the bedroom
// requirements RoyalTitleDef sets for its holder. Requirements an ideo
// precept disables are left out, and an ascetic holder has none. Zero
// values mean no such requirement.
type RoyalTitle struct {
	Definition               string
	Seniority                int
	BedroomMinArea           int
	BedroomMinImpressiveness int
	// BedroomFloored requires every room cell to carry a floor terrain.
	BedroomFloored bool
	// BedroomThings are the required furniture: each entry is met by
	// Count things of any one of its definitions.
	BedroomThings []BedroomThing
}

// BedroomThing is one royal bedroom furniture requirement.
type BedroomThing struct {
	AnyOf []Resource
	Count int
}

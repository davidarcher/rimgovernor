package policy

import (
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// RoomRole is RimWorld's own RoomRoleDef defName, as the native room census
// reports Room.Role. The game scores every role worker against a room's
// contents and picks one: a room with beds is a Bedroom or Barracks whatever
// else it holds, so a role is a fact about the whole room, never something the
// controller assigns.
type RoomRole string

const (
	RoomRoleNone           RoomRole = "None"
	RoomRoleRoom           RoomRole = "Room"
	RoomRoleBedroom        RoomRole = "Bedroom"
	RoomRolePrisonCell     RoomRole = "PrisonCell"
	RoomRoleDiningRoom     RoomRole = "DiningRoom"
	RoomRoleRecRoom        RoomRole = "RecRoom"
	RoomRoleHospital       RoomRole = "Hospital"
	RoomRoleLaboratory     RoomRole = "Laboratory"
	RoomRoleWorkshop       RoomRole = "Workshop"
	RoomRoleStoreroom      RoomRole = "Storeroom"
	RoomRoleBarracks       RoomRole = "Barracks"
	RoomRolePrisonBarracks RoomRole = "PrisonBarracks"
	RoomRoleKitchen        RoomRole = "Kitchen"
	RoomRoleTomb           RoomRole = "Tomb"
	RoomRoleBarn           RoomRole = "Barn"
	// Royalty
	RoomRoleThroneRoom RoomRole = "ThroneRoom"
	// Ideology
	RoomRoleWorshipRoom RoomRole = "WorshipRoom"
	// Biotech
	RoomRoleNursery          RoomRole = "Nursery"
	RoomRolePlayroom         RoomRole = "Playroom"
	RoomRoleClassroom        RoomRole = "Classroom"
	RoomRoleDeathrestChamber RoomRole = "DeathrestChamber"
	// Anomaly
	RoomRoleContainmentCell   RoomRole = "ContainmentCell"
	RoomRoleCeremonialChamber RoomRole = "CeremonialChamber"
)

// Room is one proper indoor room from the same-tick native census. Only a
// selected building's admitted footprint becomes a reservation; room identity
// is ephemeral within the current native room graph.
type Room struct {
	ID          string
	Role        domain.Fact[RoomRole]
	Enclosed    domain.Fact[bool]
	Temperature domain.Fact[float64]
	// Cleanliness is RimWorld's own room Cleanliness stat (0 clean, negative
	// dirtier), the same number the game's food-poisoning and infection
	// chances read. Unknown when the native stat read failed.
	Cleanliness domain.Fact[float64]
	Beds        []string
	Contents    domain.Fact[[]Amount]
	Cells       []domain.Cell
}

type RoomObservation struct {
	Rooms        []Room
	EligibleBeds domain.Fact[[]string]
}

// Room returns the census row for one native room ID.
func (v RoomObservation) Room(id string) (Room, bool) {
	for _, room := range v.Rooms {
		if room.ID == id {
			return room, true
		}
	}
	return Room{}, false
}

// FacilityStatus is one row of the per-role implementation matrix.
type FacilityStatus string

const (
	// FacilityImplemented roles have a routine planner that observes their
	// demand and stages, furnishes or reuses a room for them.
	FacilityImplemented FacilityStatus = "implemented"
	// FacilityPending roles are catalogued but no planner pursues them yet.
	FacilityPending FacilityStatus = "pending"
)

// FacilityRequirement is what a routine planner needs before it can pursue a
// room role: the native content the role belongs to (empty for Core), which
// other roles may host the same function without a dedicated room, and the
// furniture definitions whose presence gives the room its role natively.
type FacilityRequirement struct {
	Role    RoomRole
	Status  FacilityStatus
	Content string
	// Compatible lists roles whose room may host this function as a shared
	// room. A hosting room keeps whatever role the game scores highest, so a
	// table in a rec room leaves it a RecRoom while still seating diners.
	Compatible []RoomRole
	// Furniture lists the building definitions the implemented planner may
	// place; an empty list marks a pending role.
	Furniture []string
}

// Hosts reports whether a room of the given role can serve this function.
func (f FacilityRequirement) Hosts(role RoomRole) bool {
	if role == f.Role {
		return true
	}
	for _, r := range f.Compatible {
		if r == role {
			return true
		}
	}
	return false
}

// FacilityCatalog is the per-role implementation matrix: every RoomRoleDef the
// installed game and expansions define, with its planner status. Roles a
// planner pursues appear as implemented with their furniture; every other
// role stays an explicit pending row rather than a silent gap. Content-gated
// roles are only pursued when their definitions are present in the planning
// census, never assumed from the row alone.
func FacilityCatalog() []FacilityRequirement {
	generic := []RoomRole{RoomRoleRoom}
	return []FacilityRequirement{
		{Role: RoomRoleDiningRoom, Status: FacilityImplemented, Compatible: append([]RoomRole{RoomRoleRecRoom}, generic...), Furniture: []string{"Table1x2c", "DiningChair"}},
		{Role: RoomRoleRecRoom, Status: FacilityImplemented, Compatible: append([]RoomRole{RoomRoleDiningRoom}, generic...), Furniture: []string{"HorseshoesPin"}},
		// A bedroom is a hosted colonist bed: MaintainSleeping stages one in
		// any room that already sleeps colonists or in a generic room, then
		// assigns it; the game scores the room Bedroom or Barracks by count.
		{Role: RoomRoleBedroom, Status: FacilityImplemented, Compatible: append([]RoomRole{RoomRoleBarracks}, generic...), Furniture: SleepingBedDefinitions},
		{Role: RoomRoleBarracks, Status: FacilityPending},
		{Role: RoomRolePrisonCell, Status: FacilityPending},
		{Role: RoomRolePrisonBarracks, Status: FacilityPending},
		// A hospital is a hosted medical bed, not a dedicated room: the game
		// scores a room holding any ordinary bed a Bedroom or Barracks, and a
		// bed flagged medical inside it still draws patients, doctors and the
		// room's cleanliness into tending (issue #4 M3). Doctor coverage is
		// AssignWork's standing requirement; medicine is MaintainMedicalReserves.
		{Role: RoomRoleHospital, Status: FacilityImplemented, Compatible: append([]RoomRole{RoomRoleBedroom, RoomRoleBarracks}, generic...), Furniture: HospitalBedDefinitions},
		{Role: RoomRoleLaboratory, Status: FacilityPending},
		// A workshop shares the starter shell: the ladder furnishes the first
		// enclosed room rather than siting a second ring (routine_sleeping.go),
		// and once the sleeping spots move indoors the game scores that room a
		// Barracks while the bench keeps working (issue #4 M2 runs 24-25).
		{Role: RoomRoleWorkshop, Status: FacilityImplemented, Compatible: append([]RoomRole{RoomRoleBarracks}, generic...), Furniture: []string{"CraftingSpot", "TableStonecutter"}},
		{Role: RoomRoleStoreroom, Status: FacilityPending},
		{Role: RoomRoleKitchen, Status: FacilityPending},
		{Role: RoomRoleTomb, Status: FacilityPending},
		{Role: RoomRoleBarn, Status: FacilityPending},
		{Role: RoomRoleThroneRoom, Status: FacilityPending, Content: "Royalty"},
		{Role: RoomRoleWorshipRoom, Status: FacilityPending, Content: "Ideology"},
		{Role: RoomRoleNursery, Status: FacilityPending, Content: "Biotech"},
		{Role: RoomRolePlayroom, Status: FacilityPending, Content: "Biotech"},
		{Role: RoomRoleClassroom, Status: FacilityPending, Content: "Biotech"},
		{Role: RoomRoleDeathrestChamber, Status: FacilityPending, Content: "Biotech"},
		{Role: RoomRoleContainmentCell, Status: FacilityPending, Content: "Anomaly"},
		{Role: RoomRoleCeremonialChamber, Status: FacilityPending, Content: "Anomaly"},
	}
}

// Facility looks up one catalog row.
func Facility(role RoomRole) (FacilityRequirement, error) {
	for _, f := range FacilityCatalog() {
		if f.Role == role {
			return f, nil
		}
	}
	return FacilityRequirement{}, errors.New("room role is not catalogued")
}

// HostingCells returns every cell of a room whose native role can host the
// requirement, in census order. An unknown role never hosts: a facility placed
// there could be scored into an incompatible role the controller cannot see.
func HostingCells(f FacilityRequirement, rooms RoomObservation) []domain.Cell {
	var cells []domain.Cell
	for _, room := range rooms.Rooms {
		if role, known := room.Role.Value(); known && f.Hosts(role) {
			cells = append(cells, room.Cells...)
		}
	}
	return cells
}

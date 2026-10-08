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
	RoomRoleNone       RoomRole = "None"
	RoomRoleRoom       RoomRole = "Room"
	RoomRoleBedroom    RoomRole = "Bedroom"
	RoomRolePrisonCell RoomRole = "PrisonCell"
	RoomRoleDiningRoom RoomRole = "DiningRoom"
	RoomRoleRecRoom    RoomRole = "RecRoom"
	RoomRoleHospital   RoomRole = "Hospital"
	RoomRoleLaboratory RoomRole = "Laboratory"
	RoomRoleWorkshop   RoomRole = "Workshop"
	RoomRoleStoreroom  RoomRole = "Storeroom"
	RoomRoleBarracks   RoomRole = "Barracks"
	// RoomRoleShelter is a plan role only (#2041): the temporary starter room,
	// scored by the game as whatever its furniture makes it.
	RoomRoleShelter        RoomRole = "Shelter"
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
	// RoomRoleIsolationRoom is a plan role only (#1740), no game RoomRoleDef:
	// the game scores the furnished room a bedroom, so it has no facility row.
	RoomRoleIsolationRoom RoomRole = "IsolationRoom"
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
	// Roofed is every cell roofed (native open roof count zero).
	Roofed domain.Fact[bool]
	// Doors are the doors in the room's boundary (#1323).
	Doors                                           []RoomDoor
	Burning, TemperatureControl, PerishableContents domain.Fact[bool]
	Pawns                                           []domain.PawnID
}

// RoomDoor is one door in a room's boundary: the door cell and the cell
// across it from the room. Outdoors is whether that far side is outdoors;
// EnemyFacing is set by MarkEnemyDoors.
type RoomDoor struct {
	ID                                                  string
	Cell                                                domain.Cell
	Outside                                             domain.Cell
	Outdoors                                            domain.Fact[bool]
	EnemyFacing                                         bool
	PlayerOwned, Open, HoldOpen, BlockedOpen, Forbidden domain.Fact[bool]
}

// KillboxCells are the cells of the plan's killbox reservation; nil when
// the plan reserves none.
func (p LayoutPlan) KillboxCells() []domain.Cell {
	var out []domain.Cell
	for _, r := range p.Reservations {
		if r.Kind != ReserveKillbox {
			continue
		}
		for x := r.Area.X; x < r.Area.X+r.Area.Width; x++ {
			for z := r.Area.Z; z < r.Area.Z+r.Area.Height; z++ {
				out = append(out, domain.Cell{X: x, Z: z})
			}
		}
	}
	return out
}

// MarkEnemyDoors flags each door that opens outdoors toward the enemy
// side: its outward direction has a positive dot product toward the
// killbox centre. With no killbox every outdoor-opening door counts.
func MarkEnemyDoors(rooms RoomObservation, killbox []domain.Cell) RoomObservation {
	var cx, cz float64
	for _, c := range killbox {
		cx += float64(c.X)
		cz += float64(c.Z)
	}
	if len(killbox) > 0 {
		cx, cz = cx/float64(len(killbox)), cz/float64(len(killbox))
	}
	out := rooms
	out.Rooms = make([]Room, len(rooms.Rooms))
	for i, room := range rooms.Rooms {
		doors := make([]RoomDoor, len(room.Doors))
		for j, d := range room.Doors {
			outdoors, known := d.Outdoors.Value()
			d.EnemyFacing = known && outdoors
			if d.EnemyFacing && len(killbox) > 0 {
				dx, dz := float64(d.Outside.X-d.Cell.X), float64(d.Outside.Z-d.Cell.Z)
				d.EnemyFacing = dx*(cx-float64(d.Cell.X))+dz*(cz-float64(d.Cell.Z)) > 0
			}
			doors[j] = d
		}
		room.Doors = doors
		out.Rooms[i] = room
	}
	return out
}

type RoomObservation struct {
	Rooms        []Room
	EligibleBeds domain.Fact[[]string]
	// Dining is the dining and recreation furniture the catalog rows name; it
	// rides with the rooms because the interior plans read it per room.
	Dining DiningFurniture
	// Shapes are the plannable shapes of the catalog's buildable definitions,
	// for the same reason.
	Shapes PieceShapes
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
	// FurnitureFromGame marks an implemented role whose furniture the
	// planner reads from the game's definitions rather than listing here.
	FurnitureFromGame bool
	// Roles lists the catalog room-role furniture the planner places (the
	// native definition catalog assigns each definition its roles); an
	// implemented role names Furniture, Roles or FurnitureFromGame.
	Roles []FurnitureRole
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
		{Role: RoomRoleDiningRoom, Status: FacilityImplemented, Compatible: append([]RoomRole{RoomRoleRecRoom}, generic...), FurnitureFromGame: true},
		{Role: RoomRoleRecRoom, Status: FacilityImplemented, Compatible: append([]RoomRole{RoomRoleDiningRoom}, generic...), FurnitureFromGame: true},
		// A bedroom is a hosted colonist bed: MaintainHousing stages one in
		// any room that already sleeps colonists or in a generic room, then
		// assigns it; the game scores the room Bedroom or Barracks by count.
		{Role: RoomRoleBedroom, Status: FacilityImplemented, Compatible: append([]RoomRole{RoomRoleBarracks}, generic...), FurnitureFromGame: true},
		{Role: RoomRoleBarracks, Status: FacilityPending},
		{Role: RoomRoleShelter, Status: FacilityPending},
		// A jail is its own planned room (#880): MaintainPopulation shells it
		// while a prisoner is held and keeps a prisoner bed per prisoner.
		{Role: RoomRolePrisonCell, Status: FacilityImplemented, FurnitureFromGame: true},
		{Role: RoomRolePrisonBarracks, Status: FacilityPending},
		// A hospital is a hosted medical bed, not a dedicated room: the game
		// scores a room holding any ordinary bed a Bedroom or Barracks, and a
		// bed flagged medical inside it still draws patients, doctors and the
		// room's cleanliness into tending (issue #4 M3). Doctor coverage is
		// AssignWork's standing requirement; medicine is MaintainMedicalReserves.
		{Role: RoomRoleHospital, Status: FacilityImplemented, Compatible: append([]RoomRole{RoomRoleBedroom, RoomRoleBarracks}, generic...), FurnitureFromGame: true},
		// A laboratory is a hosted research bench: EnsureResearch stages the
		// simple bench in the shelter (scored a Barracks once the bunks stand) when a ladder rung waits on it (#254).
		{Role: RoomRoleLaboratory, Status: FacilityImplemented, Compatible: append([]RoomRole{RoomRoleWorkshop, RoomRoleBarracks}, generic...), FurnitureFromGame: true},
		// A workshop shares the shelter: the ladder furnishes the first
		// enclosed room rather than siting a second ring (rounds_sleeping.go),
		// and once the sleeping spots move indoors the game scores that room a
		// Barracks while the bench keeps working (issue #4 M2 runs 24-25).
		{Role: RoomRoleWorkshop, Status: FacilityImplemented, Compatible: append([]RoomRole{RoomRoleBarracks}, generic...), FurnitureFromGame: true},
		{Role: RoomRoleStoreroom, Status: FacilityPending},
		// A kitchen is its own planned room (#835): EnsureCooking shells it
		// at Masonry and above and holds the stove to its interior.
		{Role: RoomRoleKitchen, Status: FacilityImplemented, FurnitureFromGame: true},
		// A tomb is its own planned room (#832): MaintainBurial shells it and
		// places a sarcophagus while a dead colonist has none waiting.
		{Role: RoomRoleTomb, Status: FacilityImplemented, FurnitureFromGame: true},
		// A barn is a planned room beside the pen (#1633): the animal planner
		// shells it and places an animal sleeping spot per kept animal.
		{Role: RoomRoleBarn, Status: FacilityImplemented, FurnitureFromGame: true},
		// A throne room is its own planned room (#1601): MaintainHousing shells
		// it sized to the next title's area, places a throne and furnishes it to
		// the title's impressiveness; assigning the throne awaits an action kind.
		{Role: RoomRoleThroneRoom, Status: FacilityImplemented, Content: "Royalty", Furniture: []string{"Throne", "GrandThrone"}},
		// A worship room is its own planned room (#1658), staged like the child
		// rooms: MaintainHousing shells it and places the buildings the
		// ideoligion requires, which the game's defs name (the ideology
		// section), not this row.
		{Role: RoomRoleWorshipRoom, Status: FacilityImplemented, Content: "Ideology", FurnitureFromGame: true},
		// The child rooms are their own planned rooms (#1680): MaintainHousing
		// shells each while a baby (nursery), baby or child (playroom) or child
		// (classroom) lives and places the furniture the game scores the role
		// from (ChildRoomNeeds); a nursery room holds no other bed.
		{Role: RoomRoleNursery, Status: FacilityImplemented, Content: "Biotech", Roles: []FurnitureRole{RoleBabyBed}},
		{Role: RoomRolePlayroom, Status: FacilityImplemented, Content: "Biotech", Roles: []FurnitureRole{RoleToy, RoleDecoration}},
		{Role: RoomRoleClassroom, Status: FacilityImplemented, Content: "Biotech", Roles: []FurnitureRole{RoleBoard, RoleDesk}},
		// A deathrest chamber is its own planned room (#1690): MaintainHousing
		// shells it while a deathrester lives and places a casket per
		// deathrester and the accelerators its capacity allows.
		{Role: RoomRoleDeathrestChamber, Status: FacilityImplemented, Content: "Biotech", Roles: []FurnitureRole{RoleDeathrestCasket, RoleDeathrestAccelerator}},
		// A containment cell is its own planned room (#1741): MaintainHousing
		// shells it while a capturable entity has no platform able to hold it
		// and places the holding platform the game's defs name, once the
		// predicted containment strength reaches what the entity needs.
		{Role: RoomRoleContainmentCell, Status: FacilityImplemented, Content: "Anomaly", FurnitureFromGame: true},
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

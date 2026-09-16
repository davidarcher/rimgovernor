package domain

import "errors"

// RoomPurpose is the player's stated reason for one requested room shell. It
// is the Go form of Python player_commands.BuildRoom.purpose, and like that
// field it selects nothing native: it records why the player asked, so a later
// reviewer can tell a shelter request from a storage one.
type RoomPurpose string

const (
	ShelterRoom    RoomPurpose = "shelter"
	DefenseRoom    RoomPurpose = "defense"
	ProductionRoom RoomPurpose = "production"
	StorageRoom    RoomPurpose = "storage"
	ComfortRoom    RoomPurpose = "comfort"
)

func (p RoomPurpose) Validate() error {
	switch p {
	case ShelterRoom, DefenseRoom, ProductionRoom, StorageRoom, ComfortRoom:
		return nil
	}
	return errors.New("unsupported room purpose")
}

// RoomBounds is one requested room rectangle, the Go form of Python
// colony_plan.RoomBounds: a nonnegative anchor and a 4..64 extent on each
// axis. The lower bound of four is what gives the shell an interior at all;
// anything smaller is a wall segment, not a room.
type RoomBounds struct{ X, Z, Width, Height int32 }

func (b RoomBounds) Validate() error {
	if b.X < 0 || b.Z < 0 {
		return errors.New("room anchor must be nonnegative")
	}
	if b.Width < 4 || b.Width > 64 || b.Height < 4 || b.Height > 64 {
		return errors.New("a room shell needs an interior; use a building batch for a wall segment")
	}
	return nil
}

// RoomShell is one requested perimeter of walls with a single entrance door,
// the Go form of Python colony_plan.RoomShell. It is deliberately comparable
// so a submission can be replayed by value, exactly as ZoneCreate is.
//
// Unlike the Python contract it carries one material rather than a preference
// list: this controller's native building boundary dispatches domain.Building,
// which resolves exactly one stuff (empty requesting the native default), so a
// list whose entries after the first could never be dispatched would be state
// nothing reads. Every placement takes this one material, which is what the
// Python algorithm does with its whole list per placement.
//
// Nothing here establishes that the rectangle is buildable. Native footprint,
// terrain, reachability, cost and placement legality are established at
// inspection and dispatch for each expanded Building, never inferred here.
type RoomShell struct {
	bounds   RoomBounds
	wallDef  string
	doorDef  string
	material string
	entrance Rotation
	purpose  RoomPurpose
}

// NewRoomShell mirrors Python RoomShell's own validator: distinct wall and
// door definitions (a perimeter of doors is not a wall shell), an interior,
// and one of the four cardinal entrance sides.
func NewRoomShell(bounds RoomBounds, wallDef, doorDef, material string, entrance Rotation, purpose RoomPurpose) (RoomShell, error) {
	if err := bounds.Validate(); err != nil {
		return RoomShell{}, err
	}
	if !nativeText(wallDef, false) || !nativeText(doorDef, false) || !nativeText(material, true) {
		return RoomShell{}, errors.New("invalid native wall, door or material text")
	}
	if wallDef == doorDef {
		return RoomShell{}, errors.New("a room shell needs distinct wall and entrance definitions")
	}
	switch entrance {
	case North, East, South, West:
	default:
		return RoomShell{}, errors.New("invalid room entrance side")
	}
	if err := purpose.Validate(); err != nil {
		return RoomShell{}, err
	}
	return RoomShell{bounds, wallDef, doorDef, material, entrance, purpose}, nil
}

func (r RoomShell) Bounds() RoomBounds     { return r.bounds }
func (r RoomShell) WallDefinition() string { return r.wallDef }
func (r RoomShell) DoorDefinition() string { return r.doorDef }

// Empty Material requests the native default material selection, exactly as
// Building.Stuff does.
func (r RoomShell) Material() string     { return r.material }
func (r RoomShell) Entrance() Rotation   { return r.entrance }
func (r RoomShell) Purpose() RoomPurpose { return r.purpose }

// Set reports whether a shell was requested at all, the same absence test
// GoalKind.Set offers for its own optional Proposal field.
func (r RoomShell) Set() bool { return r.wallDef != "" }

// Door is the port of Python spatial.room_entrance's door cell: the midpoint
// of the named side, taken with integer division exactly as Python's
// b.x+b.width//2 and b.z+b.height//2 do. Nonnegative bounds make Go's
// truncating division and Python's floor division agree.
func (r RoomShell) Door() Cell {
	b := r.bounds
	switch r.entrance {
	case North:
		return Cell{X: b.X + b.Width/2, Z: b.Z + b.Height - 1}
	case South:
		return Cell{X: b.X + b.Width/2, Z: b.Z}
	case East:
		return Cell{X: b.X + b.Width - 1, Z: b.Z + b.Height/2}
	default:
		return Cell{X: b.X, Z: b.Z + b.Height/2}
	}
}

// Placements is the port of Python spatial.room_placements: every perimeter
// cell of the rectangle, the entrance cell carrying the door definition and
// the requested entrance rotation and every other carrying the wall definition
// facing north, with the door first. Interior cells are deliberately absent --
// the Python algorithm builds a shell, not a floor.
//
// Cell order matches Python's: the door, then the remaining perimeter in
// RoomBounds.cells() order (z outer, x inner), which is what its stable
// door-first sort leaves behind.
//
// The largest supported rectangle, 64 by 64, expands to 252 placements, within
// the 256-action bound a committed plan may hold.
func (r RoomShell) Placements() []Building {
	if !r.Set() {
		return nil
	}
	footprint, err := RectangleFootprint(r.bounds, r.entrance)
	if err != nil {
		return nil
	}
	return footprint.Placements(r.wallDef, r.doorDef, r.material)
}

// ValidateRoomIntent enforces Python BuildRoom.intent_id's exact pattern,
// ^[a-zA-Z0-9_-]{1,40}$. The intent is the durable player-facing handle for
// one requested construction: a later cancellation or relocation slice
// resolves it back to the committed plan, the way Python's
// plan.control['player_intents'] mapping does.
func ValidateRoomIntent(id string) error {
	if len(id) == 0 || len(id) > 40 {
		return errors.New("invalid construction intent identity")
	}
	for _, r := range id {
		if (r < 'a' || r > 'z') && (r < 'A' || r > 'Z') && (r < '0' || r > '9') && r != '_' && r != '-' {
			return errors.New("invalid construction intent identity")
		}
	}
	return nil
}

// ReconstructRoomShell rebuilds a canonical shell from its exported fields,
// mirroring ReconstructZone. A value that does not survive the round trip was
// never constructed through NewRoomShell.
func ReconstructRoomShell(r RoomShell) (RoomShell, error) {
	return NewRoomShell(r.bounds, r.wallDef, r.doorDef, r.material, r.entrance, r.purpose)
}

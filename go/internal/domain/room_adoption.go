package domain

import "errors"

// RoomAdoption is one already-built native room the player has inspected and
// explicitly claims as satisfying EnsureInitialShelter. It is the Go form of
// Python player_commands.AdoptRoom.
//
// Unlike RoomShell it names no wall, door or material definition and expands to
// no placements: adoption issues no construction at all. The rectangle and
// entrance side are the player's own inspection of a room whose walls, door and
// roof are already complete -- built by the autopilot, by an earlier BuildRoom,
// or by hand in game. Python's handler says exactly this in its own reply:
// "Existing construction preserved; no new construction issued".
//
// The optional interior is Python's nonrectangular support. When absent the
// interior is the rectangle's inside, the cells strictly within the perimeter;
// when present it is the exact observed cell set, and an exact observed boundary
// door cell must accompany it. Python requires both or neither, and this does
// the same.
//
// Nothing here establishes that the room is real. Native enclosure, roof
// coverage and doorway classification are the game's to report; see
// go/README.md's G01.08 entry for why this controller does not re-observe them
// at submission the way Python's room_adoption.verified_room does.
type RoomAdoption struct {
	bounds   RoomBounds
	entrance Rotation
	// interior is the exact observed nonrectangular interior, nil for the
	// ordinary rectangular interior. It is held by value in a slice, so
	// RoomAdoption is not comparable; use SameRoomAdoption rather than ==.
	interior []Cell
	door     Cell
	doorSet  bool
}

// NewRoomAdoption ports Python AdoptRoom's `geometry` model validator exactly.
//
// The rectangular form takes no interior and no entrance cell, and its door is
// the midpoint of the named side, the same cell RoomShell.Door reports.
//
// The nonrectangular form requires both the interior and the entrance cell, and
// enforces Python's four cross-field rules in order: no duplicate cell; every
// cell strictly inside the inspected bounds; the entrance cell itself outside
// the interior with the cell beyond it also outside, so the door is a boundary
// facing out rather than an interior aisle; and the interior connected as one
// region reachable from the cell just inside the entrance.
func NewRoomAdoption(bounds RoomBounds, entrance Rotation, interior []Cell, door Cell, doorSet bool) (RoomAdoption, error) {
	if err := bounds.Validate(); err != nil {
		return RoomAdoption{}, err
	}
	switch entrance {
	case North, East, South, West:
	default:
		return RoomAdoption{}, errors.New("invalid room entrance side")
	}
	if (interior != nil) != doorSet {
		return RoomAdoption{}, errors.New("nonrectangular adoption requires both exact interior cells and an entrance cell")
	}
	if interior == nil {
		return RoomAdoption{bounds: bounds, entrance: entrance}, nil
	}
	// Python bounds the list at 1..3844 (62 by 62, the largest interior a 64 by
	// 64 inspected rectangle can hold).
	if len(interior) == 0 || len(interior) > 3844 {
		return RoomAdoption{}, errors.New("adopted interior cell count out of range")
	}
	points := make(map[Cell]bool, len(interior))
	for _, cell := range interior {
		if points[cell] {
			return RoomAdoption{}, errors.New("duplicate adopted room cell")
		}
		if !(bounds.X < cell.X && cell.X < bounds.X+bounds.Width-1 && bounds.Z < cell.Z && cell.Z < bounds.Z+bounds.Height-1) {
			return RoomAdoption{}, errors.New("adopted interior cells must lie inside the inspected bounds")
		}
		points[cell] = true
	}
	step := entrance.outward()
	inside := Cell{X: door.X - step.X, Z: door.Z - step.Z}
	beyond := Cell{X: door.X + step.X, Z: door.Z + step.Z}
	if points[door] || points[beyond] || !connectedCells(inside, points) {
		return RoomAdoption{}, errors.New("adopted geometry needs a connected interior and a boundary entrance facing outside")
	}
	held := make([]Cell, len(interior))
	copy(held, interior)
	return RoomAdoption{bounds: bounds, entrance: entrance, interior: held, door: door, doorSet: true}, nil
}

// outward is the unit step from inside the room out through an entrance on this
// side, the Go form of Python's {'north':(0,1),'south':(0,-1),'east':(1,0),
// 'west':(-1,0)} mapping.
func (r Rotation) outward() Cell {
	switch r {
	case North:
		return Cell{Z: 1}
	case South:
		return Cell{Z: -1}
	case East:
		return Cell{X: 1}
	default:
		return Cell{X: -1}
	}
}

// connectedCells is the port of Python shell_site.connected_cells: the
// orthogonally connected region of points reachable from seed. It reports
// whether that region is the whole set, which is the only question the caller
// asks, so it allocates one visited map and no result slice.
func connectedCells(seed Cell, points map[Cell]bool) bool {
	if !points[seed] {
		return false
	}
	seen := map[Cell]bool{seed: true}
	queue := []Cell{seed}
	for len(queue) > 0 {
		at := queue[len(queue)-1]
		queue = queue[:len(queue)-1]
		for _, next := range []Cell{{at.X + 1, at.Z}, {at.X - 1, at.Z}, {at.X, at.Z + 1}, {at.X, at.Z - 1}} {
			if points[next] && !seen[next] {
				seen[next] = true
				queue = append(queue, next)
			}
		}
	}
	return len(seen) == len(points)
}

func (r RoomAdoption) Bounds() RoomBounds    { return r.bounds }
func (r RoomAdoption) Entrance() Rotation    { return r.entrance }
func (r RoomAdoption) Rectangular() bool     { return r.interior == nil }
func (r RoomAdoption) EntranceCellSet() bool { return r.doorSet }

// Set reports whether an adoption was requested at all, the same absence test
// RoomShell.Set offers.
func (r RoomAdoption) Set() bool { return r.bounds.Width != 0 }

// InteriorCells is the exact observed nonrectangular interior as supplied, nil
// for the rectangular form. Callers must not retain or mutate the returned
// slice; it is freshly allocated per call.
func (r RoomAdoption) InteriorCells() []Cell {
	if r.interior == nil {
		return nil
	}
	out := make([]Cell, len(r.interior))
	copy(out, r.interior)
	return out
}

// Interior is the cell set this adoption claims: the exact observed cells when
// they were supplied, and otherwise the rectangle strictly inside the perimeter,
// the same set Python's verified_room derives. Callers must not retain or mutate
// the returned slice.
func (r RoomAdoption) Interior() []Cell {
	if r.interior != nil {
		return r.InteriorCells()
	}
	b := r.bounds
	out := make([]Cell, 0, (b.Width-2)*(b.Height-2))
	for z := b.Z + 1; z < b.Z+b.Height-1; z++ {
		for x := b.X + 1; x < b.X+b.Width-1; x++ {
			out = append(out, Cell{X: x, Z: z})
		}
	}
	return out
}

// EntranceCell is the boundary door cell: the exact observed one when supplied,
// and otherwise the midpoint of the named side. The rectangular case is
// deliberately the same integer-division midpoint RoomShell.Door places its own
// door at, so adopting a room this controller built names that room's own door.
func (r RoomAdoption) EntranceCell() Cell {
	if r.doorSet {
		return r.door
	}
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

// SameRoomAdoption is value equality for a type a slice field makes
// incomparable. Submission replay compares requests by value, so it needs this
// rather than ==. Cell order is significant: the request the player made is
// reported back as they sent it.
func SameRoomAdoption(a, b RoomAdoption) bool {
	if a.bounds != b.bounds || a.entrance != b.entrance || a.door != b.door || a.doorSet != b.doorSet || len(a.interior) != len(b.interior) || (a.interior == nil) != (b.interior == nil) {
		return false
	}
	for i := range a.interior {
		if a.interior[i] != b.interior[i] {
			return false
		}
	}
	return true
}

// ReconstructRoomAdoption rebuilds a canonical adoption from its own fields,
// mirroring ReconstructRoomShell. A value that does not survive the round trip
// was never constructed through NewRoomAdoption.
func ReconstructRoomAdoption(r RoomAdoption) (RoomAdoption, error) {
	return NewRoomAdoption(r.bounds, r.entrance, r.InteriorCells(), r.door, r.doorSet)
}

// AdoptionEvidence is the player's inspection of the native room being adopted,
// the Go form of Python's goal.evidence['adoption'] record: which native room
// they looked at, how many cells it held, and the native role the game reported
// for it.
//
// It is carried as stored evidence rather than on domain.Goal because
// domain.Goal has no evidence or target field at all -- the same constraint the
// CreateGoal slice documented for its own per-goal configuration. Making Goal
// carry free-form evidence would change a type the autopilot's whole goal
// lifecycle validates; this keeps the evidence beside the goal instead, in the
// adoption's own table, exactly as the population and resource policy slices
// keep their config-only state.
type AdoptionEvidence struct {
	RoomID    string
	Cells     int32
	Role      string
	RoleLabel string
}

func (e AdoptionEvidence) Validate() error {
	if !validID(e.RoomID) {
		return errors.New("adoption evidence needs the observed native room identity")
	}
	// One cell is the smallest interior a 4 by 4 inspected rectangle holds; the
	// upper bound matches NewRoomAdoption's own interior bound.
	if e.Cells < 1 || e.Cells > 3844 {
		return errors.New("adopted native room cell count out of range")
	}
	if !nativeText(e.Role, true) || !nativeText(e.RoleLabel, true) {
		return errors.New("invalid adopted native room role")
	}
	return nil
}

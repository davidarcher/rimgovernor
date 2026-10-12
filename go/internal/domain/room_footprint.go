package domain

import (
	"errors"
	"sort"
)

// RoomFootprint is one exact-cell room: an orthogonally connected interior,
// the wall ring that encloses it and a single boundary door. It is the
// geometry every room shape shares, whether it came from a rectangle, an
// ellipse template or an irregular footprint grown over constrained terrain.
//
// The wall ring is the interior's 8-neighbourhood boundary: every cell that
// touches the interior orthogonally or diagonally and is not interior itself.
// The orthogonal boundary alone would already enclose (RimWorld regions are
// 4-connected and a pawn may not cut a corner between two touching walls), but
// filling the diagonal cells gives the same solid ring a rectangle's corners
// have, so a rectangle, a concave template and a grown irregular room all expand by one
// rule. The largest interior is 3844 cells, the same bound RoomAdoption
// enforces.
//
// Nothing here establishes that the cells are buildable. Native footprint,
// terrain, reachability, cost and placement legality are established at
// inspection and dispatch for each expanded Building, never inferred here.
type RoomFootprint struct {
	interior []Cell
	walls    []Cell
	door     Cell
	entrance Rotation
	// extra are the boundary doors after the first, in the order given; nil
	// for a single-door room.
	extra []RoomDoor
}

// RoomDoor is one boundary door of a footprint: its wall cell and the side
// it faces out of the room.
type RoomDoor struct {
	Cell     Cell
	Entrance Rotation
}

// cellBefore orders cells z outer, x inner, the order RoomShell.Placements and
// Python RoomBounds.cells() use.
func cellBefore(a, b Cell) bool { return a.Z < b.Z || a.Z == b.Z && a.X < b.X }

func neighbours8(c Cell) [8]Cell {
	return [8]Cell{{c.X + 1, c.Z}, {c.X + 1, c.Z + 1}, {c.X, c.Z + 1}, {c.X - 1, c.Z + 1}, {c.X - 1, c.Z}, {c.X - 1, c.Z - 1}, {c.X, c.Z - 1}, {c.X + 1, c.Z - 1}}
}

// NewRoomFootprint validates one exact interior and its entrance. The
// interior must be nonempty, duplicate free, clear of the map edge by one
// cell for its walls and orthogonally connected. The door
// must be a wall cell whose inward neighbour (against the entrance side) is
// interior and whose outward neighbour is neither interior nor wall, so the
// door is a boundary facing out rather than an interior aisle. The wall ring
// plus door must fit a committed plan: at most 255 placements. Each extra
// door follows the same rule and must be distinct from the others.
func NewRoomFootprint(interior []Cell, door Cell, entrance Rotation, extra ...RoomDoor) (RoomFootprint, error) {
	var zero RoomFootprint
	switch entrance {
	case North, East, South, West:
	default:
		return zero, errors.New("invalid room entrance side")
	}
	if len(interior) == 0 || len(interior) > 3844 {
		return zero, errors.New("room interior cell count out of range")
	}
	inside := make(map[Cell]bool, len(interior))
	for _, cell := range interior {
		if cell.X < 1 || cell.Z < 1 {
			return zero, errors.New("room interior must leave space for its walls")
		}
		if inside[cell] {
			return zero, errors.New("duplicate room interior cell")
		}
		inside[cell] = true
	}
	if !connectedCells(interior[0], inside) {
		return zero, errors.New("room interior must be orthogonally connected")
	}
	wall := map[Cell]bool{}
	for cell := range inside {
		for _, next := range neighbours8(cell) {
			if !inside[next] {
				wall[next] = true
			}
		}
	}
	seen := map[Cell]bool{}
	for _, d := range append([]RoomDoor{{door, entrance}}, extra...) {
		switch d.Entrance {
		case North, East, South, West:
		default:
			return zero, errors.New("invalid room entrance side")
		}
		step := d.Entrance.outward()
		in := Cell{X: d.Cell.X - step.X, Z: d.Cell.Z - step.Z}
		out := Cell{X: d.Cell.X + step.X, Z: d.Cell.Z + step.Z}
		if !wall[d.Cell] || !inside[in] || inside[out] || wall[out] || seen[d.Cell] {
			return zero, errors.New("room door must be a wall cell facing outside")
		}
		seen[d.Cell] = true
	}
	if len(wall) > 255 {
		return zero, errors.New("room wall ring exceeds the committed plan bound")
	}
	walls := make([]Cell, 0, len(wall))
	for cell := range wall {
		walls = append(walls, cell)
	}
	sort.Slice(walls, func(i, j int) bool { return cellBefore(walls[i], walls[j]) })
	held := make([]Cell, 0, len(inside))
	for cell := range inside {
		held = append(held, cell)
	}
	sort.Slice(held, func(i, j int) bool { return cellBefore(held[i], held[j]) })
	return RoomFootprint{interior: held, walls: walls, door: door, entrance: entrance, extra: append([]RoomDoor(nil), extra...)}, nil
}

// RoofSupported reports whether every interior cell lies within maxDistance
// (the game's RoofCollapseUtility.RoofMaxSupportDistance, the catalog's
// roof_max_support_distance) of some wall cell, so the game's automatic
// roofing covers the completed shell without columns. A player may still
// request a larger rectangle explicitly; the autopilot never proposes one.
func (f RoomFootprint) RoofSupported(maxDistance float64) bool {
	for _, cell := range f.interior {
		supported := false
		for _, w := range f.walls {
			dx, dz := int64(cell.X)-int64(w.X), int64(cell.Z)-int64(w.Z)
			if float64(dx*dx+dz*dz) <= maxDistance*maxDistance {
				supported = true
				break
			}
		}
		if !supported {
			return false
		}
	}
	return f.Set()
}

// Set reports whether a footprint was constructed at all.
func (f RoomFootprint) Set() bool          { return len(f.interior) > 0 }
func (f RoomFootprint) Door() Cell         { return f.door }
func (f RoomFootprint) Entrance() Rotation { return f.entrance }

// ExtraDoors returns the doors beyond the primary one, nil for a single-door
// room.
func (f RoomFootprint) ExtraDoors() []RoomDoor { return append([]RoomDoor(nil), f.extra...) }

// Doors returns every door, the primary first.
func (f RoomFootprint) Doors() []RoomDoor {
	return append([]RoomDoor{{f.door, f.entrance}}, f.extra...)
}

// Threshold is the cell straight outside the door, the ground the room is
// entered from.
func (f RoomFootprint) Threshold() Cell { return RoomDoor{f.door, f.entrance}.Threshold() }

// Threshold is the cell straight outside the door.
func (d RoomDoor) Threshold() Cell {
	step := d.Entrance.outward()
	return Cell{X: d.Cell.X + step.X, Z: d.Cell.Z + step.Z}
}

// Interior and Walls return fresh copies in z-outer, x-inner order.
func (f RoomFootprint) Interior() []Cell { return append([]Cell(nil), f.interior...) }
func (f RoomFootprint) Walls() []Cell    { return append([]Cell(nil), f.walls...) }

// Cells returns every cell the shell occupies, interior and wall ring
// together, for site legality checks.
func (f RoomFootprint) Cells() []Cell {
	cells := make([]Cell, 0, len(f.interior)+len(f.walls))
	cells = append(cells, f.interior...)
	return append(cells, f.walls...)
}

// Bounds is the smallest rectangle covering interior and walls.
func (f RoomFootprint) Bounds() RoomBounds {
	if !f.Set() {
		return RoomBounds{}
	}
	minX, minZ, maxX, maxZ := f.walls[0].X, f.walls[0].Z, f.walls[0].X, f.walls[0].Z
	for _, c := range f.walls {
		minX, maxX = min(minX, c.X), max(maxX, c.X)
		minZ, maxZ = min(minZ, c.Z), max(maxZ, c.Z)
	}
	return RoomBounds{X: minX, Z: minZ, Width: maxX - minX + 1, Height: maxZ - minZ + 1}
}

// SameRoomFootprint reports whether two footprints hold identical geometry.
func SameRoomFootprint(a, b RoomFootprint) bool {
	if a.door != b.door || a.entrance != b.entrance || len(a.extra) != len(b.extra) || len(a.interior) != len(b.interior) || len(a.walls) != len(b.walls) {
		return false
	}
	for i := range a.interior {
		if a.interior[i] != b.interior[i] {
			return false
		}
	}
	for i := range a.walls {
		if a.walls[i] != b.walls[i] {
			return false
		}
	}
	for i := range a.extra {
		if a.extra[i] != b.extra[i] {
			return false
		}
	}
	return true
}

// Placements expands the shell: the door first, carrying the door definition
// and the entrance rotation, then every other wall cell carrying the wall
// definition facing north, in z-outer, x-inner order. Interior cells are
// deliberately absent -- this is a shell, not a floor.
func (f RoomFootprint) Placements(wallDef, doorDef, material string) []Building {
	return f.StyledPlacements(ShellStyle{WallDef: wallDef, DoorDef: doorDef, DoorStuff: material, WallStuff: material})
}

// ShellStyle is the definitions and stuff a shell is expanded with: the
// wall and door definitions and the door and wall stuff.
type ShellStyle struct {
	WallDef, DoorDef, DoorStuff, WallStuff string
}

// StyledPlacements is Placements with the style choosing the stuff: the door
// first with its own stuff, then every wall cell with the wall stuff. An
// unset footprint expands to nothing.
func (f RoomFootprint) StyledPlacements(style ShellStyle) []Building {
	if !f.Set() {
		return nil
	}
	first, err := NewBuilding(style.DoorDef, f.door, f.entrance, style.DoorStuff)
	if err != nil {
		return nil
	}
	doors := map[Cell]bool{f.door: true}
	placements := make([]Building, 0, len(f.walls))
	placements = append(placements, first)
	for _, d := range f.extra {
		door, err := NewBuilding(style.DoorDef, d.Cell, d.Entrance, style.DoorStuff)
		if err != nil {
			return nil
		}
		doors[d.Cell] = true
		placements = append(placements, door)
	}
	for _, cell := range f.walls {
		if doors[cell] {
			continue
		}
		wall, err := NewBuilding(style.WallDef, cell, North, style.WallStuff)
		if err != nil {
			return nil
		}
		placements = append(placements, wall)
	}
	return placements
}

// RectangleFootprint is the rectangular shell RoomShell describes: bounds
// cover walls and interior, the door sits at the midpoint of the entrance
// side exactly as RoomShell.Door reports.
func RectangleFootprint(bounds RoomBounds, entrance Rotation) (RoomFootprint, error) {
	if err := bounds.Validate(); err != nil {
		return RoomFootprint{}, err
	}
	interior := make([]Cell, 0, int(bounds.Width-2)*int(bounds.Height-2))
	for z := bounds.Z + 1; z < bounds.Z+bounds.Height-1; z++ {
		for x := bounds.X + 1; x < bounds.X+bounds.Width-1; x++ {
			interior = append(interior, Cell{X: x, Z: z})
		}
	}
	var door Cell
	switch entrance {
	case North:
		door = Cell{X: bounds.X + bounds.Width/2, Z: bounds.Z + bounds.Height - 1}
	case South:
		door = Cell{X: bounds.X + bounds.Width/2, Z: bounds.Z}
	case East:
		door = Cell{X: bounds.X + bounds.Width - 1, Z: bounds.Z + bounds.Height/2}
	case West:
		door = Cell{X: bounds.X, Z: bounds.Z + bounds.Height/2}
	default:
		return RoomFootprint{}, errors.New("invalid room entrance side")
	}
	return NewRoomFootprint(interior, door, entrance)
}

// RoomBounds is one room rectangle: a nonnegative anchor and a 4..64 extent on
// each axis. The lower bound of four is what gives a shell an interior at all.
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

// connectedCells reports whether the orthogonally connected region of points
// reachable from seed is the whole set. It allocates one visited map and no
// result slice.
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

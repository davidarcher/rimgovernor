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
// have, so a rectangle, an oval and a grown irregular room all expand by one
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
}

// roofSupportDistance is RimWorld's RoofCollapseUtility support radius: a
// roof cell further than this from any wall collapses. Autopilot shapes keep
// every interior cell within it so a completed shell auto-roofs without
// columns; see RoofSupported.
const roofSupportDistance = 6

// cellBefore orders cells z outer, x inner, the order RoomShell.Placements and
// Python RoomBounds.cells() use.
func cellBefore(a, b Cell) bool { return a.Z < b.Z || a.Z == b.Z && a.X < b.X }

func neighbours4(c Cell) [4]Cell {
	return [4]Cell{{c.X + 1, c.Z}, {c.X, c.Z + 1}, {c.X - 1, c.Z}, {c.X, c.Z - 1}}
}

func neighbours8(c Cell) [8]Cell {
	return [8]Cell{{c.X + 1, c.Z}, {c.X + 1, c.Z + 1}, {c.X, c.Z + 1}, {c.X - 1, c.Z + 1}, {c.X - 1, c.Z}, {c.X - 1, c.Z - 1}, {c.X, c.Z - 1}, {c.X + 1, c.Z - 1}}
}

// NewRoomFootprint validates one exact interior and its entrance. The
// interior must be nonempty, duplicate free, clear of the map edge by one
// cell for its walls and orthogonally connected. The door
// must be a wall cell whose inward neighbour (against the entrance side) is
// interior and whose outward neighbour is neither interior nor wall, so the
// door is a boundary facing out rather than an interior aisle. The wall ring
// plus door must fit a committed plan: at most 255 placements.
func NewRoomFootprint(interior []Cell, door Cell, entrance Rotation) (RoomFootprint, error) {
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
	step := entrance.outward()
	in := Cell{X: door.X - step.X, Z: door.Z - step.Z}
	out := Cell{X: door.X + step.X, Z: door.Z + step.Z}
	if !wall[door] || !inside[in] || inside[out] || wall[out] {
		return zero, errors.New("room door must be a wall cell facing outside")
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
	return RoomFootprint{interior: held, walls: walls, door: door, entrance: entrance}, nil
}

// RoofSupported reports whether every interior cell lies within RimWorld's
// roof support distance of some wall cell, so the game's automatic roofing
// covers the completed shell without columns. A player may still request a
// larger rectangle explicitly; the autopilot never proposes one.
func (f RoomFootprint) RoofSupported() bool {
	for _, cell := range f.interior {
		supported := false
		for _, w := range f.walls {
			dx, dz := int64(cell.X)-int64(w.X), int64(cell.Z)-int64(w.Z)
			if dx*dx+dz*dz <= roofSupportDistance*roofSupportDistance {
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

// Threshold is the cell straight outside the door, the ground the room is
// entered from.
func (f RoomFootprint) Threshold() Cell {
	step := f.entrance.outward()
	return Cell{X: f.door.X + step.X, Z: f.door.Z + step.Z}
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
	if a.door != b.door || a.entrance != b.entrance || len(a.interior) != len(b.interior) || len(a.walls) != len(b.walls) {
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
	return true
}

// Placements expands the shell: the door first, carrying the door definition
// and the entrance rotation, then every other wall cell carrying the wall
// definition facing north, in z-outer, x-inner order. Interior cells are
// deliberately absent -- this is a shell, not a floor.
func (f RoomFootprint) Placements(wallDef, doorDef, material string) []Building {
	if !f.Set() {
		return nil
	}
	first, err := NewBuilding(doorDef, f.door, f.entrance, material)
	if err != nil {
		return nil
	}
	placements := make([]Building, 0, len(f.walls))
	placements = append(placements, first)
	for _, cell := range f.walls {
		if cell == f.door {
			continue
		}
		wall, err := NewBuilding(wallDef, cell, North, material)
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

// EllipseOrientation names the direction of an ellipse's radiusZ axis:
// straight along the map's z axis, along x, or along one of the two
// diagonals. A circle is the same under every orientation.
type EllipseOrientation string

const (
	// EllipseNorthSouth keeps radiusX along x and radiusZ along z.
	EllipseNorthSouth EllipseOrientation = "north_south"
	// EllipseEastWest swaps the axes: radiusZ runs along x.
	EllipseEastWest EllipseOrientation = "east_west"
	// EllipseNorthEast runs radiusZ along the x+z diagonal.
	EllipseNorthEast EllipseOrientation = "north_east"
	// EllipseNorthWest runs radiusZ along the x-z diagonal.
	EllipseNorthWest EllipseOrientation = "north_west"
)

func (o EllipseOrientation) Validate() error {
	switch o {
	case EllipseNorthSouth, EllipseEastWest, EllipseNorthEast, EllipseNorthWest:
		return nil
	}
	return errors.New("unsupported ellipse orientation")
}

// EllipseFootprint generates a circular or oval hut interior from integer
// arithmetic alone, so the same request yields the same cells on every run.
// A cell belongs to the interior when its centre lies on or inside the
// ellipse: axis-aligned, dx²·rz² + dz²·rx² ≤ rx²·rz²; diagonal, with u the
// offset across the radiusZ diagonal and v the offset along it,
// u²·rz² + v²·rx² ≤ 2·rx²·rz². Radii are 2..30 cells and measure the
// Euclidean half-axes, so a north-east oval with radiusZ 5 reaches about
// 3.5 cells along x and z together. The door is the entrance-side wall cell
// nearest the centre axis with clear ground outside.
func EllipseFootprint(center Cell, radiusX, radiusZ int32, orientation EllipseOrientation, entrance Rotation) (RoomFootprint, error) {
	if err := orientation.Validate(); err != nil {
		return RoomFootprint{}, err
	}
	if radiusX < 2 || radiusX > 30 || radiusZ < 2 || radiusZ > 30 {
		return RoomFootprint{}, errors.New("ellipse radii must be 2..30 cells")
	}
	interior := EllipseInterior(center, radiusX, radiusZ, orientation)
	footprint, err := NewRoomFootprint(interior, ellipseDoor(center, interior, entrance), entrance)
	if err != nil {
		return RoomFootprint{}, err
	}
	if !footprint.RoofSupported() {
		return RoomFootprint{}, errors.New("ellipse interior exceeds roof support")
	}
	return footprint, nil
}

// EllipseInterior is the cell set of EllipseFootprint without its wall ring
// or door: the cells whose centres lie on or inside the ellipse, in z-outer,
// x-inner order. Radii below one, above 30 or an unknown orientation yield
// nil. Excavation reuses it to carve round rooms out of rock, where the
// surrounding rock is the wall.
func EllipseInterior(center Cell, radiusX, radiusZ int32, orientation EllipseOrientation) []Cell {
	if orientation.Validate() != nil || radiusX < 1 || radiusX > 30 || radiusZ < 1 || radiusZ > 30 {
		return nil
	}
	rx, rz := int64(radiusX), int64(radiusZ)
	if orientation == EllipseEastWest {
		rx, rz = rz, rx
	}
	reach := max(rx, rz)
	var interior []Cell
	for dz := -reach; dz <= reach; dz++ {
		for dx := -reach; dx <= reach; dx++ {
			var inside bool
			switch orientation {
			case EllipseNorthEast:
				u, v := dx-dz, dx+dz
				inside = u*u*rz*rz+v*v*rx*rx <= 2*rx*rx*rz*rz
			case EllipseNorthWest:
				u, v := dx+dz, dx-dz
				inside = u*u*rz*rz+v*v*rx*rx <= 2*rx*rx*rz*rz
			default:
				inside = dx*dx*rz*rz+dz*dz*rx*rx <= rx*rx*rz*rz
			}
			if inside {
				interior = append(interior, Cell{X: center.X + int32(dx), Z: center.Z + int32(dz)})
			}
		}
	}
	return interior
}

// ellipseDoor picks the entrance-side wall cell nearest the centre axis that
// has interior directly inside and open ground directly outside. On a
// diagonal oval the ring is two cells thick where the slope crosses the axis,
// so the cell straight out from the centre may be backed by another wall; the
// nearest cell along the side whose outward neighbour is clear is taken
// instead, ties resolved toward the smaller coordinate.
func ellipseDoor(center Cell, interior []Cell, entrance Rotation) Cell {
	inside := make(map[Cell]bool, len(interior))
	for _, c := range interior {
		inside[c] = true
	}
	wall := map[Cell]bool{}
	for c := range inside {
		for _, next := range neighbours8(c) {
			if !inside[next] {
				wall[next] = true
			}
		}
	}
	step := entrance.outward()
	var door Cell
	best := int64(-1)
	for w := range wall {
		in := Cell{X: w.X - step.X, Z: w.Z - step.Z}
		out := Cell{X: w.X + step.X, Z: w.Z + step.Z}
		if !inside[in] || inside[out] || wall[out] {
			continue
		}
		off := int64(w.X - center.X)
		if step.X != 0 {
			off = int64(w.Z - center.Z)
		}
		if off < 0 {
			off = -off
		}
		if best < 0 || off < best || off == best && cellBefore(w, door) {
			best, door = off, w
		}
	}
	return door
}

// GrowFootprint carves a connected interior over constrained terrain: a
// breadth-first walk from seed, in east, north, west, south order, until
// target interior cells are held. A cell joins the interior only when it and
// all eight of its neighbours are free, so the wall ring that surrounds the
// result is buildable by construction and the room hugs whatever blocks it
// without ever needing a wall on rock or water. The door is the first wall
// cell, scanning entrance sides south, east, north then west and cells in
// z-outer, x-inner order, whose inward neighbour is interior and whose outward
// neighbour is free. It reports false when no such room of at least nine
// cells exists.
func GrowFootprint(seed Cell, free func(Cell) bool, target int) (RoomFootprint, bool) {
	if free == nil || target < 9 || target > 3844 {
		return RoomFootprint{}, false
	}
	admissible := func(c Cell) bool {
		if c.X < 1 || c.Z < 1 || !free(c) {
			return false
		}
		for _, next := range neighbours8(c) {
			if !free(next) {
				return false
			}
		}
		return true
	}
	if !admissible(seed) {
		return RoomFootprint{}, false
	}
	inside := map[Cell]bool{seed: true}
	interior := []Cell{seed}
	for i := 0; i < len(interior) && len(interior) < target; i++ {
		for _, next := range neighbours4(interior[i]) {
			if len(interior) >= target {
				break
			}
			if inside[next] || !admissible(next) {
				continue
			}
			inside[next] = true
			interior = append(interior, next)
		}
	}
	if len(interior) < 9 {
		return RoomFootprint{}, false
	}
	wall := map[Cell]bool{}
	for _, cell := range interior {
		for _, next := range neighbours8(cell) {
			if !inside[next] {
				wall[next] = true
			}
		}
	}
	walls := make([]Cell, 0, len(wall))
	for cell := range wall {
		walls = append(walls, cell)
	}
	sort.Slice(walls, func(i, j int) bool { return cellBefore(walls[i], walls[j]) })
	for _, entrance := range []Rotation{South, East, North, West} {
		step := entrance.outward()
		for _, door := range walls {
			in := Cell{X: door.X - step.X, Z: door.Z - step.Z}
			out := Cell{X: door.X + step.X, Z: door.Z + step.Z}
			if !inside[in] || inside[out] || wall[out] || !free(out) {
				continue
			}
			footprint, err := NewRoomFootprint(interior, door, entrance)
			if err != nil || !footprint.RoofSupported() {
				continue
			}
			return footprint, true
		}
	}
	return RoomFootprint{}, false
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

// InteriorRect is a rectangle of interior cells, the building block of a
// composite room. Unlike RoomBounds it holds no walls: the ring is grown
// around the union afterwards.
type InteriorRect struct{ X, Z, Width, Height int32 }

func (r InteriorRect) cells() []Cell {
	cells := make([]Cell, 0, int(r.Width)*int(r.Height))
	for z := r.Z; z < r.Z+r.Height; z++ {
		for x := r.X; x < r.X+r.Width; x++ {
			cells = append(cells, Cell{X: x, Z: z})
		}
	}
	return cells
}

// UnionFootprint joins one to eight rectangles of interior cells into one
// room: an L-shaped chamber, a chamber with a wing, two chambers joined by a
// narrow connector. The parts may overlap or touch; their union must be
// orthogonally connected and, like every autopilot shape, roof itself. The
// door stands on the bounding box's entrance edge (never in a notch, where
// it would open into the room's own recess), on the wall cell nearest the
// box's centre line whose inward cell has interior on both sides across the
// entrance axis and interior again behind it -- never a corner and never
// the mouth or flank of a one-cell connector, which is the room's aisle --
// and whose outward cell is clear.
func UnionFootprint(parts []InteriorRect, entrance Rotation) (RoomFootprint, error) {
	if len(parts) == 0 || len(parts) > 8 {
		return RoomFootprint{}, errors.New("a composite room takes one to eight parts")
	}
	inside := map[Cell]bool{}
	var interior []Cell
	for _, part := range parts {
		if part.X < 1 || part.Z < 1 || part.Width < 1 || part.Height < 1 || part.Width > 62 || part.Height > 62 {
			return RoomFootprint{}, errors.New("composite room part out of range")
		}
		for _, cell := range part.cells() {
			if !inside[cell] {
				inside[cell] = true
				interior = append(interior, cell)
			}
		}
	}
	door, ok := unionDoor(interior, inside, entrance)
	if !ok {
		return RoomFootprint{}, errors.New("composite room has no door site on its entrance side")
	}
	footprint, err := NewRoomFootprint(interior, door, entrance)
	if err != nil {
		return RoomFootprint{}, err
	}
	if !footprint.RoofSupported() {
		return RoomFootprint{}, errors.New("composite room interior exceeds roof support")
	}
	return footprint, nil
}

// unionDoor is UnionFootprint's door rule over an interior set.
func unionDoor(interior []Cell, inside map[Cell]bool, entrance Rotation) (Cell, bool) {
	wall := map[Cell]bool{}
	minX, minZ, maxX, maxZ := interior[0].X, interior[0].Z, interior[0].X, interior[0].Z
	for c := range inside {
		minX, maxX = min(minX, c.X), max(maxX, c.X)
		minZ, maxZ = min(minZ, c.Z), max(maxZ, c.Z)
		for _, next := range neighbours8(c) {
			if !inside[next] {
				wall[next] = true
			}
		}
	}
	// The centre line in cell units doubled, so an even extent needs no
	// rounding: |2·coordinate - (min+max)|.
	axis := int64(minX + maxX)
	step := entrance.outward()
	side := Cell{X: 1}
	if step.X != 0 {
		axis = int64(minZ + maxZ)
		side = Cell{Z: 1}
	}
	var edge Cell
	switch entrance {
	case North:
		edge = Cell{Z: maxZ + 1}
	case South:
		edge = Cell{Z: minZ - 1}
	case East:
		edge = Cell{X: maxX + 1}
	default:
		edge = Cell{X: minX - 1}
	}
	var door Cell
	best := int64(-1)
	for w := range wall {
		if step.X != 0 && w.X != edge.X || step.Z != 0 && w.Z != edge.Z {
			continue
		}
		in := Cell{X: w.X - step.X, Z: w.Z - step.Z}
		out := Cell{X: w.X + step.X, Z: w.Z + step.Z}
		if !inside[in] || inside[out] || wall[out] {
			continue
		}
		deeper := Cell{X: in.X - step.X, Z: in.Z - step.Z}
		if !inside[deeper] || !inside[Cell{X: in.X + side.X, Z: in.Z + side.Z}] || !inside[Cell{X: in.X - side.X, Z: in.Z - side.Z}] {
			continue
		}
		off := 2*int64(w.X) - axis
		if step.X != 0 {
			off = 2*int64(w.Z) - axis
		}
		if off < 0 {
			off = -off
		}
		if best < 0 || off < best || off == best && cellBefore(w, door) {
			best, door = off, w
		}
	}
	return door, best >= 0
}

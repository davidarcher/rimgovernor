package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ColonyGrid is the shared layout geometry every site search snaps to
// (#605): an origin cell, a pitch and two perpendicular unit axes. A grid
// line runs through every cell whose offset from the origin along an axis
// is a multiple of the pitch; the module between two lines is an
// 11x11 interior with its own wall ring (13 cells) and a 3-wide aisle. The
// pitch is the same at every build tier: Camp ignores the grid and higher
// tiers fill module sub-cells (SubCells), so once a grid is established it
// never moves.
type ColonyGrid struct {
	Origin domain.Cell
	Pitch  int32
	// Axes are the grid's first and second axis as unit map vectors, each
	// along one map axis; the default is +X and +Z.
	Axes [2]domain.Cell
	// Source names the evidence that fixed the origin.
	Source ColonyGridSource
}

// ColonyGridSource is the evidence a derived grid's origin came from.
type ColonyGridSource string

const (
	// ColonyGridFromStarter sets the origin at the starter shell's
	// south-west exterior corner.
	ColonyGridFromStarter ColonyGridSource = "starter_shell"
	// ColonyGridFromRoom sets the origin at the largest player room's
	// south-west exterior corner.
	ColonyGridFromRoom ColonyGridSource = "largest_room"
)

const (
	// GridPitch is one module plus one aisle: 11 interior cells, two
	// walls and a 3-wide aisle. 11x11 is the largest interior RimWorld roofs
	// without a pillar (roof support reaches 6 cells from a wall), the lit
	// disc of one sun lamp, and it subdivides into 5+1+5 with the divider
	// walls on the module's own sub-grid. Conduits run inside the walls,
	// never in the aisle, so power never dictates the pitch.
	GridPitch int32 = 16
	// ColonyGridInterior is a module's interior width; ColonyGridModule is
	// its exterior width, walls included; ColonyGridAisle the aisle width.
	ColonyGridInterior int32 = 11
	ColonyGridModule   int32 = ColonyGridInterior + 2
	ColonyGridAisle    int32 = GridPitch - ColonyGridModule
	// ColonyGridSubCell is a half-module interior; the divider wall between
	// two halves is the module's middle interior row.
	ColonyGridSubCell int32 = 5
)

// ColonyGridAxes are the default axes: the first along +X, the second
// along +Z.
var ColonyGridAxes = [2]domain.Cell{{X: 1, Z: 0}, {X: 0, Z: 1}}

// District is the grid district a cell belongs to. Until C6 (#603) every
// cell is in the single core district.
type District string

const DistrictCore District = "core"

// Valid reports a positive pitch and two perpendicular unit axes, each
// along one map axis. Unset axes stand for the map's own (ColonyGridAxes),
// so a grid built from just an origin and pitch is the map-aligned grid.
func (g ColonyGrid) Valid() bool {
	unit := func(a domain.Cell) bool {
		return (a.X == 0) != (a.Z == 0) && max(a.X, -a.X) <= 1 && max(a.Z, -a.Z) <= 1
	}
	axes := g.axes()
	return g.Pitch > 0 && unit(axes[0]) && unit(axes[1]) && axes[0].X*axes[1].X+axes[0].Z*axes[1].Z == 0
}

// axes returns the grid's axes, defaulting unset ones to the map's.
func (g ColonyGrid) axes() [2]domain.Cell {
	if g.Axes == [2]domain.Cell{} {
		return ColonyGridAxes
	}
	return g.Axes
}

// local returns a cell's offsets from the origin along the two axes.
func (g ColonyGrid) local(c domain.Cell) (u, v int32) {
	axes := g.axes()
	dx, dz := c.X-g.Origin.X, c.Z-g.Origin.Z
	return dx*axes[0].X + dz*axes[0].Z, dx*axes[1].X + dz*axes[1].Z
}

// cell maps grid offsets back to a map cell.
func (g ColonyGrid) cell(u, v int32) domain.Cell {
	axes := g.axes()
	return domain.Cell{X: g.Origin.X + u*axes[0].X + v*axes[1].X, Z: g.Origin.Z + u*axes[0].Z + v*axes[1].Z}
}

// floorDiv and floorMod are the pitch's floor division and remainder,
// so cells on the origin's negative side land in the module below them.
func floorDiv(a, b int32) int32 {
	q := a / b
	if a%b != 0 && (a < 0) != (b < 0) {
		q--
	}
	return q
}
func floorMod(a, b int32) int32 { return a - floorDiv(a, b)*b }

// lineOffset is the distance from an offset to its nearest grid line.
func (g ColonyGrid) lineOffset(a int32) int32 {
	r := floorMod(a, g.Pitch)
	return min(r, g.Pitch-r)
}

// nearestLine is the grid line nearest an offset; halfway rounds down.
func (g ColonyGrid) nearestLine(a int32) int32 {
	r := floorMod(a, g.Pitch)
	base := a - r
	if r > g.Pitch-r {
		return base + g.Pitch
	}
	return base
}

// Snap returns the grid intersection nearest the cell; a cell halfway
// between two lines snaps to the lower one.
func (g ColonyGrid) Snap(c domain.Cell) domain.Cell {
	if !g.Valid() {
		return c
	}
	u, v := g.local(c)
	return g.cell(g.nearestLine(u), g.nearestLine(v))
}

// CornerError is the sum of the rectangle's south-west corner offsets to
// the nearest grid line on each axis, C4's placement penalty input; zero
// means the corner sits on a grid intersection.
func (g ColonyGrid) CornerError(r Rectangle) int {
	if !g.Valid() {
		return 0
	}
	u, v := g.local(domain.Cell{X: r.X, Z: r.Z})
	return int(g.lineOffset(u)) + int(g.lineOffset(v))
}

// OnGridLine reports whether the rectangle's south-west corner lies on a
// grid intersection.
func (g ColonyGrid) OnGridLine(r Rectangle) bool { return g.Valid() && g.CornerError(r) == 0 }

// Aisles lists the aisle cells inside bounds: every cell whose offset
// along either axis falls in the pitch's last ColonyGridAisle cells
// (offsets 13, 14 and 15 of 16 from a grid line). Cells come back x-major,
// then z.
func (g ColonyGrid) Aisles(bounds Bounds) []domain.Cell {
	return g.AislesWithin(bounds, Rectangle{Width: bounds.Width, Height: bounds.Height})
}

// AislesWithin is Aisles clipped to a region (a planning window), so a
// consumer bounded by cell count asks only for the cells it plans over.
func (g ColonyGrid) AislesWithin(bounds Bounds, region Rectangle) []domain.Cell {
	if !g.Valid() || bounds.Width <= 0 || bounds.Height <= 0 {
		return nil
	}
	minX, minZ := max(region.X, 0), max(region.Z, 0)
	maxX, maxZ := min(region.X+region.Width, bounds.Width), min(region.Z+region.Height, bounds.Height)
	aisle := func(a int32) bool { return floorMod(a, g.Pitch) >= ColonyGridModule }
	out := []domain.Cell{}
	for x := minX; x < maxX; x++ {
		for z := minZ; z < maxZ; z++ {
			c := domain.Cell{X: x, Z: z}
			u, v := g.local(c)
			if aisle(u) || aisle(v) {
				out = append(out, c)
			}
		}
	}
	return out
}

// District names the district a cell belongs to.
func (g ColonyGrid) District(domain.Cell) District { return DistrictCore }

// Module is the exterior rectangle, walls included, of the module whose
// pitch square contains the cell; a cell in an aisle belongs to the module
// the aisle follows.
func (g ColonyGrid) Module(c domain.Cell) Rectangle {
	if !g.Valid() {
		return Rectangle{}
	}
	u, v := g.local(c)
	u, v = u-floorMod(u, g.Pitch), v-floorMod(v, g.Pitch)
	return g.rectangle(u, v, ColonyGridModule, ColonyGridModule)
}

// rectangle is the map rectangle covering grid offsets [u,u+w) x [v,v+h).
func (g ColonyGrid) rectangle(u, v, w, h int32) Rectangle {
	a, b := g.cell(u, v), g.cell(u+w-1, v+h-1)
	minX, minZ := min(a.X, b.X), min(a.Z, b.Z)
	return Rectangle{X: minX, Z: minZ, Width: max(a.X, b.X) - minX + 1, Height: max(a.Z, b.Z) - minZ + 1}
}

// SubCells returns the interiors one module subdivides into, in grid
// offsets from the module's exterior corner: the whole 11x11, the two
// 5x11 halves along the first axis, the two 11x5 halves along the second,
// then the four 5x5 quarters. The divider walls sit on the module's
// middle interior row and column. A rectangle that is not a module of
// this grid yields nil.
func (g ColonyGrid) SubCells(module Rectangle) []Rectangle {
	if !g.Valid() || module.Width != ColonyGridModule || module.Height != ColonyGridModule {
		return nil
	}
	if g.Module(domain.Cell{X: module.X, Z: module.Z}) != module {
		return nil
	}
	u, v := g.local(domain.Cell{X: module.X, Z: module.Z})
	// The corner cell may map to any exterior corner under flipped axes;
	// normalise to the module's lowest grid offsets.
	u, v = u-floorMod(u, g.Pitch), v-floorMod(v, g.Pitch)
	const half = ColonyGridSubCell + 1
	out := []Rectangle{g.rectangle(u+1, v+1, ColonyGridInterior, ColonyGridInterior)}
	for _, du := range []int32{0, half} {
		out = append(out, g.rectangle(u+1+du, v+1, ColonyGridSubCell, ColonyGridInterior))
	}
	for _, dv := range []int32{0, half} {
		out = append(out, g.rectangle(u+1, v+1+dv, ColonyGridInterior, ColonyGridSubCell))
	}
	for _, du := range []int32{0, half} {
		for _, dv := range []int32{0, half} {
			out = append(out, g.rectangle(u+1+du, v+1+dv, ColonyGridSubCell, ColonyGridSubCell))
		}
	}
	return out
}

// DeriveColonyGrid fixes a grid from the colony's first deliberate
// geometry: the starter shell's south-west exterior corner, or without a
// recorded starter shell the south-west corner of the largest player
// room, the largest connected wall ring in a complete construction
// census. Neither known yields an unknown grid. The tier does not change
// the pitch (GridPitch) and is accepted so the derivation's inputs
// are the tier gate's; identical inputs give identical grids.
func DeriveColonyGrid(starter domain.Fact[domain.RoomFootprint], construction CurrentConstruction, tier BuildTier) domain.Fact[ColonyGrid] {
	_ = tier
	grid := ColonyGrid{Pitch: GridPitch, Axes: ColonyGridAxes}
	if shell, known := starter.Value(); known && shell.Set() {
		b := shell.Bounds()
		grid.Origin, grid.Source = domain.Cell{X: b.X, Z: b.Z}, ColonyGridFromStarter
		return domain.Known(grid)
	}
	room, ok := largestRoom(construction)
	if !ok {
		return domain.Unknown[ColonyGrid]()
	}
	grid.Origin, grid.Source = domain.Cell{X: room.X, Z: room.Z}, ColonyGridFromRoom
	return domain.Known(grid)
}

// largestRoom is the bounding rectangle of the largest eight-connected
// component of wall and door cells in a complete colony census, by area
// then by south-west-most corner.
func largestRoom(census CurrentConstruction) (Rectangle, bool) {
	if !census.Colony {
		return Rectangle{}, false
	}
	walls := map[domain.Cell]bool{}
	for _, b := range census.Buildings {
		if !wallLike(b.Building.Definition()) {
			continue
		}
		for _, c := range b.Cells {
			walls[c] = true
		}
	}
	ordered := make([]domain.Cell, 0, len(walls))
	for c := range walls {
		ordered = append(ordered, c)
	}
	sort.Slice(ordered, func(i, j int) bool { return extentCellLess(ordered[i], ordered[j]) })
	visited := map[domain.Cell]bool{}
	var best Rectangle
	found := false
	for _, seed := range ordered {
		if visited[seed] {
			continue
		}
		queue := []domain.Cell{seed}
		visited[seed] = true
		minX, minZ, maxX, maxZ := seed.X, seed.Z, seed.X, seed.Z
		for i := 0; i < len(queue); i++ {
			c := queue[i]
			minX, maxX, minZ, maxZ = min(minX, c.X), max(maxX, c.X), min(minZ, c.Z), max(maxZ, c.Z)
			for dx := int32(-1); dx <= 1; dx++ {
				for dz := int32(-1); dz <= 1; dz++ {
					next := domain.Cell{X: c.X + dx, Z: c.Z + dz}
					if walls[next] && !visited[next] {
						visited[next] = true
						queue = append(queue, next)
					}
				}
			}
		}
		r := Rectangle{X: minX, Z: minZ, Width: maxX - minX + 1, Height: maxZ - minZ + 1}
		area, bestArea := int64(r.Width)*int64(r.Height), int64(best.Width)*int64(best.Height)
		if !found || area > bestArea || area == bestArea && extentCellLess(domain.Cell{X: r.X, Z: r.Z}, domain.Cell{X: best.X, Z: best.Z}) {
			best, found = r, true
		}
	}
	return best, found
}

// wallLike reports a definition that forms a room boundary.
func wallLike(definition string) bool {
	switch definition {
	case "Wall", "Door", "Autodoor", "Embrasure":
		return true
	}
	return false
}

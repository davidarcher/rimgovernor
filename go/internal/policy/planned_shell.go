package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

type Rectangle struct{ X, Z, Width, Height int32 }
type SiteCell struct {
	Cell                                                         domain.Cell
	Walkable, Zone, Roofed, Indoors, SupportsLight, StorageEmpty domain.Fact[bool]
	// Doorway reports a door, or a door blueprint or frame, on the cell;
	// indoor furnishing keeps the cells beside a doorway clear as its aisle.
	Doorway   domain.Fact[bool]
	Fertility domain.Fact[float64]
	Polluted  domain.Fact[bool]
	Glow      domain.Fact[float64]
	// Roof names the native roof def over the cell (rock roofs mark a
	// mountain face for excavation).
	Roof domain.Fact[string]
	// ZoneID names the native zone covering the cell when Zone is true.
	ZoneID domain.Fact[string]
	// Room names the native room holding the cell: growing-room
	// kinds pick a block per room interior.
	Room domain.Fact[string]
	// The per-cell thing list and the tile columns that join it. Terrain names the terrain def; InHome is inside the home area;
	// FoundationAffordances is the comma-joined, sorted affordances a
	// foundation may stand on; SnowDepth and TopLayerRemovable read the
	// ground. Things lists the cell's non-pawn things in native order: a
	// view into the grid's shared slab, never to be modified. Empty means
	// none on a held cell; a fogged cell is not a row at all.
	Terrain               domain.Fact[string]
	InHome                domain.Fact[bool]
	FoundationAffordances domain.Fact[string]
	SnowDepth             domain.Fact[float64]
	TopLayerRemovable     domain.Fact[bool]
	Things                []Thing `json:",omitempty"`
}

func sortedCells(set map[domain.Cell]bool) []domain.Cell {
	if len(set) == 0 {
		return nil
	}
	out := make([]domain.Cell, 0, len(set))
	for p := range set {
		out = append(out, p)
	}
	sort.Slice(out, func(i, j int) bool { return cellLess(out[i], out[j]) })
	return out
}

// starterStorage is the 3x3 indoor stockpile: the rectangle's upper-middle
// patch, or for any other shape the 3x3 nearest that position whose cells
// all lie in the interior.
func starterStorage(shell domain.RoomFootprint) Rectangle {
	b := shell.Bounds()
	inside := map[domain.Cell]bool{}
	for _, c := range shell.Interior() {
		inside[c] = true
	}
	fits := func(r Rectangle) bool {
		for _, p := range rectCells(r) {
			if !inside[p] {
				return false
			}
		}
		return true
	}
	want := Rectangle{b.X + b.Width/2 - 1, b.Z + b.Height/2 + 1, 3, 3}
	if fits(want) {
		return want
	}
	best, found := Rectangle{}, false
	for _, c := range shell.Interior() {
		r := Rectangle{c.X, c.Z, 3, 3}
		if !fits(r) {
			continue
		}
		if !found || squaredDistance(domain.Cell{X: r.X, Z: r.Z}, domain.Cell{X: want.X, Z: want.Z}) < squaredDistance(domain.Cell{X: best.X, Z: best.Z}, domain.Cell{X: want.X, Z: want.Z}) {
			best, found = r, true
		}
	}
	return best
}

func rectCells(r Rectangle) []domain.Cell {
	cells := make([]domain.Cell, 0, int(r.Width*r.Height))
	for x := r.X; x < r.X+r.Width; x++ {
		for z := r.Z; z < r.Z+r.Height; z++ {
			cells = append(cells, domain.Cell{X: x, Z: z})
		}
	}
	return cells
}
func cellLess(a, b domain.Cell) bool { return a.X < b.X || a.X == b.X && a.Z < b.Z }
func squaredDistance(a, b domain.Cell) int64 {
	x, z := int64(a.X)-int64(b.X), int64(a.Z)-int64(b.Z)
	return x*x + z*z
}

package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ShelterBunks is the sleeping layout of a starter shell before its ring
// goes up (#612): beds are the first construction on the site and the
// sleeping spots the interim on the cells left over, so the ring is raised
// around colonists who already have somewhere to lie down.
//
// Every bunk is one north-facing 1x2 footprint (the anchor and the cell
// above it), which is what a SleepingSpot or a Bed of the native default
// rotation occupies. Beds keep off the shell's corner cells (a bed against
// a ring corner is reachable diagonally from outside the wall), off the
// cell inside the door and the doorway's other neighbours (the entrance
// aisle) and off the starter storage patch; spots are gone once the beds
// stand, so they may take the corners too. Bunks are packed in z-outer,
// x-inner order, beds first, and never overlap each other or a blocked
// cell. Fewer bunks than asked for is not an error: the shell is small,
// and whatever fits is placed.
type ShelterBunks struct {
	Beds, Spots []domain.Cell
}

// BunkFootprint is the two cells a bunk anchored at c occupies.
func BunkFootprint(c domain.Cell) [2]domain.Cell {
	return [2]domain.Cell{c, {X: c.X, Z: c.Z + 1}}
}

// ShellCornerCells returns the interior cells beside a corner of the ring:
// a wall cell that touches the interior only diagonally, which a rectangle
// has four of. A rounded ring's diagonal courses have them too, though far
// fewer, so an oval leaves more of its floor to beds.
func ShellCornerCells(shell domain.RoomFootprint) []domain.Cell {
	interior := map[domain.Cell]bool{}
	for _, c := range shell.Interior() {
		interior[c] = true
	}
	corner := map[domain.Cell]bool{}
	for _, w := range shell.Walls() {
		orthogonal := false
		for _, n := range []domain.Cell{{X: w.X + 1, Z: w.Z}, {X: w.X - 1, Z: w.Z}, {X: w.X, Z: w.Z + 1}, {X: w.X, Z: w.Z - 1}} {
			orthogonal = orthogonal || interior[n]
		}
		if orthogonal {
			continue
		}
		for _, n := range []domain.Cell{{X: w.X + 1, Z: w.Z + 1}, {X: w.X - 1, Z: w.Z + 1}, {X: w.X + 1, Z: w.Z - 1}, {X: w.X - 1, Z: w.Z - 1}} {
			if interior[n] {
				corner[n] = true
			}
		}
	}
	var cells []domain.Cell
	for _, c := range shell.Interior() {
		if corner[c] {
			cells = append(cells, c)
		}
	}
	return cells
}

// PlanShelterBunks packs up to beds bed anchors and then up to spots spot
// anchors into the layout's interior, avoiding blocked cells (bunks already
// standing, other reservations).
func PlanShelterBunks(layout StarterLayout, beds, spots int, blocked []domain.Cell) ShelterBunks {
	interior := map[domain.Cell]bool{}
	for _, c := range layout.Shell.Interior() {
		interior[c] = true
	}
	taken := map[domain.Cell]bool{}
	for _, c := range blocked {
		taken[c] = true
	}
	for _, c := range rectCells(layout.Storage) {
		taken[c] = true
	}
	for _, c := range DoorwayAisles(Bounds{Width: 1 << 30, Height: 1 << 30}, []SiteCell{{Cell: layout.Shell.Door(), Doorway: domain.Known(true)}}) {
		taken[c] = true
	}
	corner := map[domain.Cell]bool{}
	for _, c := range ShellCornerCells(layout.Shell) {
		corner[c] = true
	}
	pack := func(count int, avoidCorners bool) []domain.Cell {
		var anchors []domain.Cell
		for _, c := range layout.Shell.Interior() {
			if len(anchors) >= count {
				break
			}
			f := BunkFootprint(c)
			fits := true
			for _, p := range f {
				fits = fits && interior[p] && !taken[p] && !(avoidCorners && corner[p])
			}
			if !fits {
				continue
			}
			for _, p := range f {
				taken[p] = true
			}
			anchors = append(anchors, c)
		}
		return anchors
	}
	return ShelterBunks{Beds: pack(beds, true), Spots: pack(spots, false)}
}

// BunkLayout picks the first layout whose interior holds every bunk cell
// and whose corner cells hold no bed cell: the shell a later rung raises
// around the bunks an earlier one placed. ok is false when no layout does.
func BunkLayout(layouts []StarterLayout, beds, spots []domain.Cell) (StarterLayout, bool) {
	for _, layout := range layouts {
		interior := map[domain.Cell]bool{}
		for _, c := range layout.Shell.Interior() {
			interior[c] = true
		}
		corner := map[domain.Cell]bool{}
		for _, c := range ShellCornerCells(layout.Shell) {
			corner[c] = true
		}
		fits := true
		for _, anchor := range beds {
			for _, p := range BunkFootprint(anchor) {
				fits = fits && interior[p] && !corner[p]
			}
		}
		for _, anchor := range spots {
			for _, p := range BunkFootprint(anchor) {
				fits = fits && interior[p]
			}
		}
		if fits {
			return layout, true
		}
	}
	return StarterLayout{}, false
}

package policy

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The shelter template (#2042, epic #2037): the temporary starter room holds
// a research table, a crafting spot, f.Campfires campfires (two on a cold map,
// none elsewhere, #2044) and one
// sleeping bunk per occupant. It also decides how bunks pack (it folds
// ShelterBunks, #612), so one place owns the entrance-aisle and corner rules.
//
// The research table is the only piece with a fixed place: centred on the
// back wall, its front interaction cell kept clear (the bench row of the
// workshop and laboratory templates). The crafting spot and the campfires
// take the room's corners, which bunks may not use (see below), the back
// ones first. Bunks then fill the floor back to front, left to right, each a
// 1x2 footprint facing South with its head on the cell farther from the
// entrance. A bunk never takes a corner cell (a bed in a corner is reachable
// diagonally from outside the wall), a reserved cell, the cell inside any
// door, or a cell whose loss would cut floor off from the doors. A campfire
// may sit beside bunks (nothing models campfire flammability).
//
// The bunk slots are shared: a bed replaces a sleeping spot on the same
// cells, so the template plans one set of slots per occupant whatever stands
// on them. Every piece but the bunks is optional: a cramped room, or a
// catalog without a row for it, gets the bunks alone rather than no plan.

const (
	shelterCampfireDef = "Campfire"
	shelterCraftingDef = "CraftingSpot"
	shelterCoolerDef   = "PassiveCooler"
	// shelterBunkSlotPrefix names every bunk slot ("bunk.1", "bunk.2", ...).
	shelterBunkSlotPrefix = "bunk."
)

// ShelterCoolerSlotPrefix names the passive cooler slots ("cooler.1", ...), a
// hot map's floor slot for the temperature planner (#2044).
const ShelterCoolerSlotPrefix = "cooler."

// shelterBunkSize is a bunk's North footprint: the 1x2 of a SleepingSpot, a
// Bed and a Bedroll.
var shelterBunkSize = domain.Cell{X: 1, Z: 2}

func init() {
	RegisterInteriorTemplate(RoomRoleShelter, InteriorTemplate{Name: "shelter", Plan: planShelter})
}

// IsBunk reports whether the piece is one of the shelter's bunk slots.
func (p InteriorPiece) IsBunk() bool {
	return len(p.Slot) > len(shelterBunkSlotPrefix) && p.Slot[:len(shelterBunkSlotPrefix)] == shelterBunkSlotPrefix
}

func planShelter(f InteriorFrame, piece InteriorPieceDef) ([]InteriorPiece, bool) {
	room := InteriorRoom{Interior: Rectangle{Width: f.Width, Height: f.Depth}, Doors: f.Doors}
	kept := map[domain.Cell]bool{}
	for _, c := range f.Reserved {
		kept[c] = true
	}
	blocked := map[domain.Cell]bool{}
	var pieces []InteriorPiece
	try := func(p InteriorPiece) bool {
		if !f.Contains(p.Rect) {
			return false
		}
		cells := rectCells(p.Rect)
		for _, c := range cells {
			if kept[c] || blocked[c] {
				return false
			}
		}
		if !InteriorPlacementWalkable(room, blocked, cells) {
			return false
		}
		// The worker's cell stays floor: no piece may stand on it later.
		if ic, ok := p.Interaction(); ok {
			if blocked[ic] || kept[ic] || rectContains(p.Rect, ic) {
				return false
			}
			kept[ic] = true
		}
		for _, c := range cells {
			blocked[c] = true
		}
		pieces = append(pieces, p)
		return true
	}

	if def, ok := BenchRowDef(f, piece, RoomRoleLaboratory); ok {
		if benches, _, _, ok := BenchRow(f, def, 0, 1, func(int) string { return "research" }); ok {
			for _, b := range benches {
				try(b)
			}
		}
	}

	corners := []domain.Cell{{X: 0, Z: f.Depth - 1}, {X: f.Width - 1, Z: f.Depth - 1}, {X: 0, Z: 0}, {X: f.Width - 1, Z: 0}}
	single := func(slot, def string) {
		shape, ok := f.Shapes.Get(def)
		if !ok || shape.Size != (domain.Cell{X: 1, Z: 1}) {
			return
		}
		for _, c := range corners {
			if try(NewInteriorPiece(slot, def, shape.Size, domain.North, c)) {
				return
			}
		}
		for v := f.Depth - 1; v >= 0; v-- {
			for u := int32(0); u < f.Width; u++ {
				if try(NewInteriorPiece(slot, def, shape.Size, domain.North, domain.Cell{X: u, Z: v})) {
					return
				}
			}
		}
	}
	single("craft", shelterCraftingDef)
	for i := range f.Campfires {
		single(fmt.Sprintf("campfire.%d", i+1), shelterCampfireDef)
	}
	for i := range f.Coolers {
		single(fmt.Sprintf("%s%d", ShelterCoolerSlotPrefix, i+1), shelterCoolerDef)
	}

	bunkDef := f.Shapes.Furniture.PrimaryBed()
	if piece.Family == RoomRoleBedroom && piece.Size == shelterBunkSize {
		bunkDef = piece.Def
	}
	if bunkDef == "" {
		bunkDef = SleepingSpotDefinition
	}
	isCorner := func(c domain.Cell) bool {
		return (c.X == 0 || c.X == f.Width-1) && (c.Z == 0 || c.Z == f.Depth-1)
	}
	count := 0
	for v := f.Depth - 2; v >= 0 && (f.Occupants <= 0 || count < f.Occupants); v-- {
		for u := int32(0); u < f.Width && (f.Occupants <= 0 || count < f.Occupants); u++ {
			bunk := NewInteriorPiece(fmt.Sprintf("%s%d", shelterBunkSlotPrefix, count+1), bunkDef, shelterBunkSize, domain.South, domain.Cell{X: u, Z: v})
			if isCorner(domain.Cell{X: u, Z: v}) || isCorner(domain.Cell{X: u, Z: v + 1}) {
				continue
			}
			if try(bunk) {
				count++
			}
		}
	}
	return pieces, true
}

package policy

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The shelter template: the temporary starter room holds
// a research table, a crafting spot, f.Campfires campfires (two on a cold map,
// none elsewhere) and one
// sleeping bunk per occupant. It also decides how bunks pack (it folds
// ShelterBunks), so one place owns the entrance-aisle rules.
//
// The research table is the only piece with a fixed place: centred on the
// back wall, its front interaction cell kept clear (the bench row of the
// workshop and laboratory templates). The crafting spot and the campfires
// take the room's corners, the back ones first. Bunks then fill the floor
// back to front, each row laid from its two ends toward the middle, so the
// aisle from the door stays open. Every bunk in the room has one rotation:
// 1x2 facing South (head on the cell farther from the entrance), or 2x1 facing
// East when that sleeps more colonists. A bunk never takes a reserved cell,
// the cell inside any door, or a cell whose loss would cut floor off from the
// doors. A campfire may sit beside bunks (nothing models campfire
// flammability).
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
// hot map's floor slot for the temperature planner.
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

// shelterPack is the floor state of a shelter plan in progress: the cells
// pieces must keep clear, the cells they stand on and the pieces so far.
type shelterPack struct {
	f       InteriorFrame
	room    InteriorRoom
	kept    map[domain.Cell]bool
	blocked map[domain.Cell]bool
	pieces  []InteriorPiece
}

func (s *shelterPack) clone() *shelterPack {
	c := *s
	c.kept, c.blocked = map[domain.Cell]bool{}, map[domain.Cell]bool{}
	for k := range s.kept {
		c.kept[k] = true
	}
	for k := range s.blocked {
		c.blocked[k] = true
	}
	c.pieces = append([]InteriorPiece(nil), s.pieces...)
	return &c
}

func (s *shelterPack) try(p InteriorPiece) bool {
	if !s.f.Contains(p.Rect) {
		return false
	}
	cells := rectCells(p.Rect)
	for _, c := range cells {
		if s.kept[c] || s.blocked[c] {
			return false
		}
	}
	if !InteriorPlacementWalkable(s.room, s.blocked, cells) {
		return false
	}
	// The worker's cell stays floor: no piece may stand on it later.
	if ic, ok := p.Interaction(); ok {
		if s.blocked[ic] || s.kept[ic] || rectContains(p.Rect, ic) {
			return false
		}
		s.kept[ic] = true
	}
	for _, c := range cells {
		s.blocked[c] = true
	}
	s.pieces = append(s.pieces, p)
	return true
}

// bunks fills the floor with bunks of one rotation, back to front, each row
// from its two ends toward the middle, so the floor by the entrance aisle is
// the last to fill. It stops at occupants
// (0 fills every bunk that fits) and reports how many it placed.
func (s *shelterPack) bunks(def string, rot domain.Rotation, occupants int) int {
	w, h, _ := rotatedSize(shelterBunkSize, rot)
	var starts []int32
	for lo, hi := int32(0), s.f.Width-w; lo <= hi; lo, hi = lo+1, hi-1 {
		starts = append(starts, lo)
		if hi != lo {
			starts = append(starts, hi)
		}
	}
	count := 0
	for v := s.f.Depth - h; v >= 0; v-- {
		for _, u := range starts {
			if occupants > 0 && count >= occupants {
				return count
			}
			if s.try(NewInteriorPiece(fmt.Sprintf("%s%d", shelterBunkSlotPrefix, count+1), def, shelterBunkSize, rot, domain.Cell{X: u, Z: v})) {
				count++
			}
		}
	}
	return count
}

func planShelter(f InteriorFrame, piece InteriorPieceDef) ([]InteriorPiece, bool) {
	s := &shelterPack{
		f:       f,
		room:    InteriorRoom{Interior: Rectangle{Width: f.Width, Height: f.Depth}, Doors: f.Doors},
		kept:    map[domain.Cell]bool{},
		blocked: map[domain.Cell]bool{},
	}
	for _, c := range f.Reserved {
		s.kept[c] = true
	}

	if def, ok := BenchRowDef(f, piece, RoomRoleLaboratory); ok {
		if benches, _, _, ok := BenchRow(f, def, 0, 1, func(int) string { return "research" }); ok {
			for _, b := range benches {
				s.try(b)
			}
		}
	}

	corners := []domain.Cell{{X: 0, Z: f.Depth - 1}, {X: f.Width - 1, Z: f.Depth - 1}, {X: 0, Z: 0}, {X: f.Width - 1, Z: 0}}
	single := func(slot, def string) {
		shape, ok := f.Shapes.Get(def)
		if !ok || shape.Size != (domain.Cell{X: 1, Z: 1}) {
			return
		}
		// A piece with an interaction cell faces a rotation whose cell stays on
		// the floor: the corner's first rotation would put it in the wall, and the
		// native refuses the slot once the ring stands.
		rots := []domain.Rotation{domain.North}
		if shape.Interaction != nil {
			rots = []domain.Rotation{domain.North, domain.East, domain.South, domain.West}
		}
		place := func(c domain.Cell) bool {
			for _, rot := range rots {
				p := NewInteriorPiece(slot, def, shape.Size, rot, c)
				if shape.Interaction != nil {
					off := *shape.Interaction
					p.InteractionOffset = &off
					if ic, ok := p.Interaction(); ok && !s.f.Contains(Rectangle{X: ic.X, Z: ic.Z, Width: 1, Height: 1}) {
						continue
					}
				}
				if s.try(p) {
					return true
				}
			}
			return false
		}
		for _, c := range corners {
			if place(c) {
				return
			}
		}
		for v := f.Depth - 1; v >= 0; v-- {
			for u := int32(0); u < f.Width; u++ {
				if place(domain.Cell{X: u, Z: v}) {
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
	// One rotation for the whole room: north-south unless east-west beds
	// sleep more colonists.
	ns, ew := s.clone(), s.clone()
	if ew.bunks(bunkDef, domain.East, f.Occupants) > ns.bunks(bunkDef, domain.South, f.Occupants) {
		return ew.pieces, true
	}
	return ns.pieces, true
}

package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The allowed areas of animal kinds beyond the Barn:
// companions live in the paddock yard, predators roam the map outside it.

// CompanionAreaKey is the bot area key of the Companion allowed area: the
// interior of the plan's ring, the paddock yard. Bonded non-roamers are kept
// in it (AnimalShelterChoice).
const CompanionAreaKey = "Companion"

// CompanionAreaLabel is the native label of the Companion area.
const CompanionAreaLabel = CompanionAreaKey

// WildAreaKey is the bot area key of the Wild allowed area: the map minus the
// ring interior. Predators, a tamed warg included, are kept in it so they
// never sit in the base with the herd.
const WildAreaKey = "Wild"

// WildAreaLabel is the native label of the Wild area.
const WildAreaLabel = WildAreaKey

// PaddockCells are the cells strictly inside the plan's ring, sorted row by
// row; empty for a plan without a ring.
func (p LayoutPlan) PaddockCells() []domain.Cell {
	wi, ok := planInterior(p, 0)
	if !ok {
		return nil
	}
	return wi.cells()
}

// WildCells are every cell of the map outside the paddock, row by row; empty
// for a plan without a ring.
func (p LayoutPlan) WildCells(bounds Bounds) []domain.Cell {
	inside := p.PaddockCells()
	if len(inside) == 0 {
		return nil
	}
	in := make(map[domain.Cell]bool, len(inside))
	for _, c := range inside {
		in[c] = true
	}
	out := make([]domain.Cell, 0, max(0, int(bounds.Width)*int(bounds.Height)-len(in)))
	for z := int32(0); z < bounds.Height; z++ {
		for x := int32(0); x < bounds.Width; x++ {
			if c := (domain.Cell{X: x, Z: z}); !in[c] {
				out = append(out, c)
			}
		}
	}
	return out
}

package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Mine tiers order mining designations along the plan's mining zone (#792):
// ore first, then the rock under dug rooms (walls included) so the core's
// footprint is cleared ahead of growth, then the remaining stone as demand
// calls for it. A cell off the plan, or no plan, is MineTierStone.
const (
	MineTierOre   = 0
	MineTierCore  = 1
	MineTierStone = 2
)

// MineTier ranks cell under plan. Zoning emits the ore mining zone before the
// plain-rock one, so the first of two mining zones is ore; a lone mining zone
// is ambiguous and ranks as stone.
func (p LayoutPlan) MineTier(cell domain.Cell) int {
	var mining []LayoutZone
	for _, z := range p.Zones {
		if z.Kind == ZoneMining {
			mining = append(mining, z)
		}
	}
	if len(mining) == 2 && zoneHas(mining[0], cell) {
		return MineTierOre
	}
	for _, r := range p.Rooms {
		in := r.Interior
		if r.Dug && cell.X >= in.X-1 && cell.X <= in.X+in.Width && cell.Z >= in.Z-1 && cell.Z <= in.Z+in.Height {
			return MineTierCore
		}
	}
	return MineTierStone
}

func zoneHas(z LayoutZone, cell domain.Cell) bool {
	for _, run := range z.Runs {
		if run.Z == cell.Z && cell.X >= run.X && cell.X < run.X+run.Length {
			return true
		}
	}
	return false
}

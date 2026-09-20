package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// protectedCellLimit is the bound every policy site search places on its
// Protected set; a request past it is refused outright.
const protectedCellLimit = 65536

// layoutProtected returns protected with the colony grid's aisles appended
// (#606): the walkways no routine builds on, so modules never fuse and every
// door stays reachable. It adds nothing at Camp, when the tier is unknown,
// or when no grid is known, so a tribal colony searches exactly as before.
//
// An aisle cell under a completed player building is not protected: the
// building is history, and a later tidying goal (#611) decides whether to
// re-site it. A cell under a blueprint or frame stays protected (the census
// lists built structures only), so a hold in flight never flips.
//
// The searches refuse more than protectedCellLimit protected cells; when
// the whole-map aisle set would breach it, only the aisles inside the
// planning window are protected.
func layoutProtected(facts observation.ColonyProjection, protected []domain.Cell) []domain.Cell {
	tier, tk := facts.BuildTier.Value()
	grid, gk := facts.ColonyGrid.Value()
	if !tk || !gk || tier < policy.BuildTierMasonry {
		return protected
	}
	aisles := grid.Aisles(facts.Bounds)
	if len(protected)+len(aisles) > protectedCellLimit {
		aisles = grid.AislesWithin(facts.Bounds, facts.Region)
	}
	if len(aisles) == 0 {
		return protected
	}
	built := map[domain.Cell]bool{}
	if census, known := facts.Facts.CurrentConstruction.Value(); known && census.Colony {
		for _, b := range census.Buildings {
			for _, c := range b.Cells {
				built[c] = true
			}
		}
	}
	out := make([]domain.Cell, 0, len(protected)+len(aisles))
	out = append(out, protected...)
	for _, c := range aisles {
		if !built[c] {
			out = append(out, c)
		}
	}
	return out
}

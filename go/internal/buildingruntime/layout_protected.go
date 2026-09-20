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

// layoutAlignment returns the colony grid and placement alignment weight
// the site searches score against (#607): the grid stays unknown and the
// weight zero at Camp, when the tier is unknown or when no grid is known,
// so those searches choose exactly as before.
func layoutAlignment(facts observation.ColonyProjection) (domain.Fact[policy.ColonyGrid], float64) {
	tier, tk := facts.BuildTier.Value()
	grid, gk := facts.ColonyGrid.Value()
	if !tk || !gk || tier < policy.BuildTierMasonry {
		return domain.Unknown[policy.ColonyGrid](), 0
	}
	return domain.Known(grid), policy.DefaultPlacementAlignment(tier)
}

// layoutFarmWeights are the farm planner's weights for the projection's
// build tier (#607): the default weights at Camp or an unknown tier, the
// alignment charge above.
func layoutFarmWeights(facts observation.ColonyProjection) policy.FarmSiteWeights {
	tier, _ := facts.BuildTier.Value()
	return policy.FarmSiteWeightsFor(tier)
}

// layoutAnchor is the cell a routine anchors its site search on (#609): at
// Masonry and above with a known grid, the centre of the district's module
// nearest the origin whose cells are all observed open ground and free of
// player buildings; otherwise, or when the district has no such module
// within the defense ring, the colony centre, so a Camp colony and a colony
// that has filled a district search exactly as before.
func layoutAnchor(facts observation.ColonyProjection, district policy.District) domain.Cell {
	grid, _ := layoutAlignment(facts)
	g, known := grid.Value()
	if !known {
		return facts.Center
	}
	cells := make(map[domain.Cell]policy.SiteCell, len(facts.Cells))
	for _, c := range facts.Cells {
		cells[c.Cell] = c
	}
	built := map[domain.Cell]bool{}
	if census, ok := facts.Facts.CurrentConstruction.Value(); ok && census.Colony {
		for _, b := range census.Buildings {
			for _, c := range b.Cells {
				built[c] = true
			}
		}
	}
	open := func(p domain.Cell) bool {
		c, ok := cells[p]
		if !ok || built[p] {
			return false
		}
		walkable, wk := c.Walkable.Value()
		occupied, ok := c.Occupied.Value()
		zone, zk := c.Zone.Value()
		return wk && walkable && ok && !occupied && zk && !zone
	}
	free := func(module policy.Rectangle) bool {
		for x := module.X; x < module.X+module.Width; x++ {
			for z := module.Z; z < module.Z+module.Height; z++ {
				if !open(domain.Cell{X: x, Z: z}) {
					return false
				}
			}
		}
		return true
	}
	if anchor, ok := (policy.Districts{Grid: g}).Anchor(district, free); ok {
		return anchor
	}
	return facts.Center
}

package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// protectedCellLimit is the bound every policy site search places on its
// Protected set; a request past it is refused outright.
const protectedCellLimit = 65536

// layoutAnchor is the cell a routine anchors its site search on. At
// Masonry and above it reads the v2 layout plan first (#785): the centre of
// the district's planned room (or field zone run) whose cells are all
// observed open ground and free of player buildings. Without a plan, or
// with no such slot, it falls back to the colony centre, so a Camp colony
// and a colony that has filled a district search exactly as before.
func layoutAnchor(facts observation.ColonyProjection, district policy.District) domain.Cell {
	plan, planned := facts.LayoutPlan.Value()
	if tier, ok := facts.BuildTier.Value(); !ok || tier < policy.BuildTierMasonry || !planned {
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
	if anchor, ok := plan.DistrictAnchor(district, free); ok {
		return anchor
	}
	return facts.Center
}

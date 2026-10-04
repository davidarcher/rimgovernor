package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// planCore is the persisted layout plan's core (#1534), or the colony
// centre when no plan is stored.
func planCore(facts observation.ColonyProjection) domain.Cell {
	if plan, ok := facts.LayoutPlan.Value(); ok {
		if core, ok := plan.Core(); ok {
			return core
		}
	}
	return facts.Center
}

// fieldAnchor is where pens, barns, fields, tombs and waste start their site
// search: the middle of the first free field-zone run of the layout plan, else
// the colony centre (no plan, or no free run). The plan is read at every build
// tier: gating it on Masonry stacked the pens, barn and turbines of a Camp
// colony on the map centre.
func fieldAnchor(facts observation.ColonyProjection) domain.Cell {
	if plan, ok := facts.LayoutPlan.Value(); ok {
		if anchor, ok := plan.FieldAnchor(layoutFree(facts)); ok {
			return anchor
		}
	}
	return facts.Center
}

// roomAnchor is the centre of the nearest free planned room of the role to the
// point to (reserve rooms when none is free), else the colony centre.
func roomAnchor(facts observation.ColonyProjection, role policy.ModuleRole, to domain.Cell) domain.Cell {
	if plan, ok := facts.LayoutPlan.Value(); ok {
		if anchor, ok := plan.NearestAnchor(role, to, layoutFree(facts)); ok {
			return anchor
		}
	}
	return facts.Center
}

// layoutFree reports whether a rectangle is observed open ground free of
// player buildings and zones (#785).
func layoutFree(facts observation.ColonyProjection) func(policy.Rectangle) bool {
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
	return func(module policy.Rectangle) bool {
		for x := module.X; x < module.X+module.Width; x++ {
			for z := module.Z; z < module.Z+module.Height; z++ {
				if !open(domain.Cell{X: x, Z: z}) {
					return false
				}
			}
		}
		return true
	}
}

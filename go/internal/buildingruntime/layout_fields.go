package buildingruntime

import (
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// firebreakFloors are the stone floors a firebreak is laid in (#790): a
// floor grows nothing, so the 2-wide strip between field chunks stays bare.
var firebreakFloors = []string{"FlagstoneGranite", "FlagstoneSandstone", "FlagstoneLimestone", "FlagstoneSlate", "FlagstoneMarble"}

// firebreakSite is the candidate kind the field planner enacts firebreak
// floors under.
const firebreakSite policy.SiteKind = "firebreak"

func isFirebreakFloor(definition string) bool {
	return strings.HasPrefix(definition, "Flagstone")
}

// layoutFieldPlan is the v2 plan the field planner zones from (#790): at
// Masonry and above, when the persisted plan holds field zones.
func layoutFieldPlan(facts observation.ColonyProjection) (policy.LayoutPlan, map[domain.Cell]bool, bool) {
	tier, tk := facts.BuildTier.Value()
	plan, pk := facts.LayoutPlan.Value()
	if !tk || !pk || tier < policy.BuildTierMasonry {
		return policy.LayoutPlan{}, nil, false
	}
	fields := plan.FieldCells()
	return plan, fields, len(fields) > 0
}

// layoutFieldProtected protects every observed cell outside the plan's
// field zones, so outdoor fields grow only on the planned fertility patches
// and turbine lanes, and never over a firebreak.
func layoutFieldProtected(facts observation.ColonyProjection, protected []domain.Cell) []domain.Cell {
	_, fields, ok := layoutFieldPlan(facts)
	if !ok {
		return protected
	}
	for _, c := range facts.Cells {
		if !fields[c.Cell] && len(protected) < protectedCellLimit {
			protected = append(protected, c.Cell)
		}
	}
	return protected
}

// firebreakCandidate floors the plan's firebreak cells that border a
// planted field and can still grow something (fertility above zero):
// observed walkable, unoccupied, unzoned ground. The floor is the available
// flagstone the stock holds the most of; false when there is nothing to lay
// or no flagstone is available.
func firebreakCandidate(facts observation.ColonyProjection) (policy.SiteTypeCandidate, bool) {
	plan, _, ok := layoutFieldPlan(facts)
	if !ok {
		return policy.SiteTypeCandidate{}, false
	}
	definition := firebreakFloor(facts)
	if definition == "" {
		return policy.SiteTypeCandidate{}, false
	}
	cells := make(map[domain.Cell]policy.SiteCell, len(facts.Cells))
	for _, c := range facts.Cells {
		cells[c.Cell] = c
	}
	zoned := func(p domain.Cell) bool {
		z, ok := cells[p].Zone.Value()
		return ok && z
	}
	var out []policy.SiteBuilding
	for _, p := range plan.Firebreaks() {
		c, seen := cells[p]
		if !seen {
			continue
		}
		fertility, fk := c.Fertility.Value()
		walkable, wk := c.Walkable.Value()
		occupied, ok := c.Occupied.Value()
		zone, zk := c.Zone.Value()
		if !fk || fertility <= 0 || !wk || !walkable || !ok || occupied || !zk || zone {
			continue
		}
		near := false
		for k := int32(1); k <= 2 && !near; k++ {
			near = zoned(domain.Cell{X: p.X + k, Z: p.Z}) || zoned(domain.Cell{X: p.X - k, Z: p.Z}) || zoned(domain.Cell{X: p.X, Z: p.Z + k}) || zoned(domain.Cell{X: p.X, Z: p.Z - k})
		}
		if near {
			out = append(out, policy.SiteBuilding{Definition: definition, Cell: p, Rotation: domain.North})
		}
	}
	if len(out) == 0 {
		return policy.SiteTypeCandidate{}, false
	}
	return policy.SiteTypeCandidate{Kind: firebreakSite, Buildings: out, Cells: len(out)}, true
}

// firebreakFloor picks the available flagstone whose cost resources the
// stock holds the most of; "" when none is available.
func firebreakFloor(facts observation.ColonyProjection) string {
	stock, _ := facts.Resources.Value()
	best, most := "", int64(-1)
	for _, d := range facts.Definitions {
		available, ak := d.Available.Value()
		terrain, tk := d.Terrain.Value()
		if !isFirebreakFloor(d.Name) || !ak || !available || !tk || !terrain {
			continue
		}
		have := int64(0)
		if costs, ok := d.Costs.Value(); ok {
			for _, cost := range costs {
				have += stock[cost.Resource]
			}
		}
		if have > most {
			best, most = d.Name, have
		}
	}
	return best
}

package buildingruntime

import (
	"context"
	"regexp"
	"strconv"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// layoutOccupied is occupiedCells plus the walls of the planned rooms an
// open journal plan is working on (roomMethodOrigins). Unknown when
// the census or the plan catalog is. It also returns the interior origins of
// those rooms.
func (r *Rounder) layoutOccupied(ctx context.Context, projection observation.ColonyProjection, plan policy.LayoutPlan) (map[domain.Cell]bool, map[domain.Cell]bool, bool) {
	cells, known := occupiedCells(projection)
	if !known {
		return nil, nil, false
	}
	plans, err := r.player.journal.LoadPlans(ctx)
	if err != nil {
		return nil, nil, false
	}
	origins := roomMethodOrigins(plans)
	for c := range policy.InFlightRoomCells(plan, origins) {
		cells[c] = true
	}
	return cells, origins, true
}

// roomMethodPattern matches the method ids of the plans keyed by a planned
// room's interior origin: its dig, shell, bedroom steps, and the tomb and
// jail pieces.
var roomMethodPattern = regexp.MustCompile(`^(?:plan-dig-[^-]+-[^-]+|bedroom-[^-]+|[^-]+-shell|(?:tomb|jail)-place)-(\d+)-(\d+)(?:-.*)?$`)

// roomMethodOrigins are the interior origins of the planned rooms an open
// (not retired) plan's method works on.
func roomMethodOrigins(plans []store.PlanState) map[domain.Cell]bool {
	out := map[domain.Cell]bool{}
	for _, p := range plans {
		if p.Retired {
			continue
		}
		if m := roomMethodPattern.FindStringSubmatch(string(p.Method)); m != nil {
			x, xerr := strconv.ParseInt(m[1], 10, 32)
			z, zerr := strconv.ParseInt(m[2], 10, 32)
			if xerr == nil && zerr == nil {
				out[domain.Cell{X: int32(x), Z: int32(z)}] = true
			}
		}
	}
	return out
}

// occupiedCells are the cells of ours a room must not lose: every census
// building, the full footprint of every blueprint and frame site and of every
// journal claim whose work has not closed gone (the definition's size when
// the projection read it, else the anchor), and every player edifice and
// doorway (a door blueprint or frame included) the planning cells report.
// RoomGrowth.Fixed reads it. Unknown without a complete census.
func occupiedCells(projection observation.ColonyProjection) (map[domain.Cell]bool, bool) {
	census, known := projection.Facts.CurrentConstruction.Value()
	if !known || !census.Colony {
		return nil, false
	}
	sizes := map[string]policy.Bounds{}
	for _, d := range projection.Definitions {
		if size, ok := d.Size.Value(); ok {
			sizes[d.Name] = size
		}
	}
	footprint := func(b domain.Building) []domain.Cell {
		if size, ok := sizes[b.Definition()]; ok {
			if r := policy.OccupiedRect(b.Cell(), domain.Cell{X: size.Width, Z: size.Height}, b.Rotation()); r.Width > 0 && r.Height > 0 {
				return policy.RectangleCells(r)
			}
		}
		return []domain.Cell{b.Cell()}
	}
	built := map[domain.Cell]bool{}
	for _, b := range census.Buildings {
		for _, c := range b.Cells {
			built[c] = true
		}
	}
	for _, s := range census.Sites {
		for _, c := range footprint(s.Building) {
			built[c] = true
		}
	}
	claims, _ := projection.Facts.ConstructionClaims.Value()
	for _, c := range claims {
		if policy.WorkOpen(c.Building, projection.Facts.CurrentConstruction) != policy.BuildingGone {
			for _, cell := range footprint(c.Building) {
				built[cell] = true
			}
		}
	}
	for _, c := range projection.Cells {
		edifice := c.PlayerEdifice()
		door, _ := c.Doorway.Value()
		if edifice != "" || door {
			built[c.Cell] = true
		}
	}
	return built, true
}

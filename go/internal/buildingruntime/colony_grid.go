package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// reviewColonyGrid serves the persisted colony grid (#605) on the review's
// projection. A timeline that holds no grid derives one from the starter
// shell this controller ordered (the oldest shell plan in the journal
// whose ring stands in the census) or, failing that, from the largest
// wall ring in the census, and records it; a grid once recorded is never
// re-derived. An incomplete census leaves the grid unknown for this
// review.
func (r *RoutineReviewer) reviewColonyGrid(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection) error {
	tick := projection.Identity.Tick
	record, ok, err := r.player.journal.ColonyGrid(ctx, snapshot, tick)
	if err != nil {
		return err
	}
	if !ok {
		census, known := projection.Facts.CurrentConstruction.Value()
		if !known || !census.Colony {
			return nil
		}
		starter, err := r.starterShell(ctx, projection.Bounds, census)
		if err != nil {
			return err
		}
		tier, _ := projection.BuildTier.Value()
		grid, known := policy.DeriveColonyGrid(starter, census, tier).Value()
		if !known {
			return nil
		}
		var established bool
		if record, established, err = r.player.journal.EstablishColonyGrid(ctx, snapshot, tick, grid); err != nil {
			return err
		}
		if established {
			clockEvent(ctx, "layout", "colony_grid", fmt.Sprintf("colony grid origin=(%d,%d) pitch=%d source=%s", grid.Origin.X, grid.Origin.Z, grid.Pitch, grid.Source), "origin_x", grid.Origin.X, "origin_z", grid.Origin.Z, "pitch", grid.Pitch, "source", string(grid.Source))
		}
	}
	projection.ColonyGrid = domain.Known(record.Grid)
	return nil
}

// starterShell is the first shell this controller ordered, read back from
// the journal as adoptShell reads rings: the oldest shell plan with one
// door whose ring lies in bounds and at least half stands in the census
// (the journal is not world-scoped, so a ring nothing stands on belongs to
// another world or was never built). Unknown when no such plan exists.
func (r *RoutineReviewer) starterShell(ctx context.Context, bounds policy.Bounds, census policy.CurrentConstruction) (domain.Fact[domain.RoomFootprint], error) {
	unknown := domain.Unknown[domain.RoomFootprint]()
	history, err := r.player.journal.PlanHistoryWithPrefix(ctx, shellPlanPrefix+"-", shellHistoryLimit)
	if err != nil {
		return unknown, err
	}
	standing := map[domain.Cell]string{}
	for _, b := range census.Buildings {
		for _, c := range b.Cells {
			standing[c] = b.Building.Definition()
		}
	}
	for i := len(history) - 1; i >= 0; i-- {
		var perimeter []domain.Building
		doors := 0
		for _, action := range history[i].Spec.Actions() {
			if b, ok := action.Building(); ok {
				if b.Definition() == "Door" {
					doors++
				}
				perimeter = append(perimeter, b)
			}
		}
		if doors != 1 {
			continue
		}
		matched := 0
		inBounds := true
		for _, b := range perimeter {
			c := b.Cell()
			inBounds = inBounds && c.X >= 0 && c.Z >= 0 && c.X < bounds.Width && c.Z < bounds.Height
			if standing[c] == b.Definition() {
				matched++
			}
		}
		if !inBounds || matched*2 < len(perimeter) {
			continue
		}
		if shell, ok := shellFootprint(perimeter); ok {
			return domain.Known(shell), nil
		}
	}
	return unknown, nil
}

// shellFootprint rebuilds the room a shell's placements enclose: the cells
// of the ring's bounding box that the ring seals off from its border, with
// the door and its rotation as the entrance.
func shellFootprint(perimeter []domain.Building) (domain.RoomFootprint, bool) {
	if len(perimeter) == 0 {
		return domain.RoomFootprint{}, false
	}
	ring := map[domain.Cell]bool{}
	var door domain.Building
	first := perimeter[0].Cell()
	minX, minZ, maxX, maxZ := first.X, first.Z, first.X, first.Z
	for _, b := range perimeter {
		c := b.Cell()
		ring[c] = true
		if b.Definition() == "Door" {
			door = b
		}
		minX, maxX, minZ, maxZ = min(minX, c.X), max(maxX, c.X), min(minZ, c.Z), max(maxZ, c.Z)
	}
	// Flood the box from its border through open cells; what the flood
	// never reaches is interior.
	outside := map[domain.Cell]bool{}
	var queue []domain.Cell
	seed := func(c domain.Cell) {
		if !ring[c] && !outside[c] {
			outside[c] = true
			queue = append(queue, c)
		}
	}
	for x := minX; x <= maxX; x++ {
		seed(domain.Cell{X: x, Z: minZ})
		seed(domain.Cell{X: x, Z: maxZ})
	}
	for z := minZ; z <= maxZ; z++ {
		seed(domain.Cell{X: minX, Z: z})
		seed(domain.Cell{X: maxX, Z: z})
	}
	for i := 0; i < len(queue); i++ {
		c := queue[i]
		for _, next := range [4]domain.Cell{{X: c.X - 1, Z: c.Z}, {X: c.X + 1, Z: c.Z}, {X: c.X, Z: c.Z - 1}, {X: c.X, Z: c.Z + 1}} {
			if next.X >= minX && next.X <= maxX && next.Z >= minZ && next.Z <= maxZ {
				seed(next)
			}
		}
	}
	var interior []domain.Cell
	for z := minZ; z <= maxZ; z++ {
		for x := minX; x <= maxX; x++ {
			c := domain.Cell{X: x, Z: z}
			if !ring[c] && !outside[c] {
				interior = append(interior, c)
			}
		}
	}
	shell, err := domain.NewRoomFootprint(interior, door.Cell(), door.Rotation())
	return shell, err == nil
}

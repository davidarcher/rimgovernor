package buildingruntime

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// shellInteriors lists every cell inside the bounds of each wall ring a
// plan raises, built or not: the floor a shell encloses (or will enclose
// once its walls stand) belongs to the room's own furniture, and a planner
// that only avoids the walls' footprints would site over it (the first
// field patch was laid across the hut's interior while the shell still
// waited for wood, and the stockpile and sleeping spots then found the
// room already zoned). Completed claims alone are too late for that, so the
// rings come from the shelter goal's plans while it is open, and from the
// completed claims once it is served (the goal then leaves the review, and
// the next field batch was drawn inside the finished hut).
func shellInteriors(plans []store.PlanState, claims []policy.ConstructionClaim) []domain.Cell {
	type bounds struct {
		minX, minZ, maxX, maxZ int32
	}
	byPlan := map[domain.PlanID]*bounds{}
	extend := func(id domain.PlanID, building domain.Building) {
		if def := building.Definition(); def != "Wall" && def != "Door" {
			return
		}
		cell := building.Cell()
		b := byPlan[id]
		if b == nil {
			byPlan[id] = &bounds{cell.X, cell.Z, cell.X, cell.Z}
			return
		}
		b.minX, b.minZ, b.maxX, b.maxZ = min(b.minX, cell.X), min(b.minZ, cell.Z), max(b.maxX, cell.X), max(b.maxZ, cell.Z)
	}
	for _, plan := range plans {
		id := plan.Spec.ID()
		for _, progress := range plan.Progress {
			building, isBuilding := progress.Action().Building()
			if !isBuilding || progress.View().Stage == domain.Cancelled {
				continue
			}
			extend(id, building)
		}
	}
	for _, claim := range claims {
		extend(claim.Plan, claim.Building)
	}
	ids := make([]domain.PlanID, 0, len(byPlan))
	for id := range byPlan {
		ids = append(ids, id)
	}
	sort.Slice(ids, func(i, j int) bool { return ids[i] < ids[j] })
	var cells []domain.Cell
	for _, id := range ids {
		b := byPlan[id]
		for x := b.minX; x <= b.maxX; x++ {
			for z := b.minZ; z <= b.maxZ; z++ {
				cells = append(cells, domain.Cell{X: x, Z: z})
			}
		}
	}
	return cells
}

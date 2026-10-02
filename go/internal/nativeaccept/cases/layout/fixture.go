// Package layout holds the tiered colony layout cases (#603) staged on the
// layout grid fixture hut (test/layout_grid_prepare). The former
// layout/grid field decision replays as a colony snapshot
// (buildingruntime TestLayoutGridFieldFillsPlanFieldBlocks, #982).
package layout

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

const (
	gridPrepare = "test/layout_grid_prepare"
	gridAudit   = "test/layout_grid_audit"
	// bunks is the sleeping spots the hut is furnished with: fewer than the
	// eight colonists the baseline houses, so the capacity goal has a
	// deficit to plan a second ring against from the first review.
	bunks = 5
	// blocks is the stone blocks dropped beside the hut door: a module ring
	// is 48 cells, and a stone wall costs five blocks.
	blocks = 400
)

// planned reports a plan of the sampled goal, active or retired, whose
// method matches (#987): with complete set, every one of its actions has
// completed, otherwise it need only hold actions.
func planned(sample map[string]any, match func(domain.MethodID) bool, complete bool) bool {
	plans, _ := sample["plans"].([]map[string]any)
	retired, _ := sample["retired_plans"].([]map[string]any)
	for _, plan := range append(plans, retired...) {
		method, _ := plan["method"].(string)
		actions, _ := plan["actions"].(int)
		stages, _ := plan["stages"].(map[string]int)
		if !match(domain.MethodID(method)) || actions == 0 {
			continue
		}
		if !complete || stages["completed"] == actions {
			return true
		}
	}
	return false
}

func bounding(cells []domain.Cell) policy.Rectangle {
	minX, minZ, maxX, maxZ := cells[0].X, cells[0].Z, cells[0].X, cells[0].Z
	for _, c := range cells[1:] {
		minX, maxX, minZ, maxZ = min(minX, c.X), max(maxX, c.X), min(minZ, c.Z), max(maxZ, c.Z)
	}
	return policy.Rectangle{X: minX, Z: minZ, Width: maxX - minX + 1, Height: maxZ - minZ + 1}
}

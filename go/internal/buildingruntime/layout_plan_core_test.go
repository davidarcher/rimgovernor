package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// TestPlanCoreAnchorsOnPlan: stockpiles and the cooking campfire anchor on
// the stored plan's storeroom, not the colonists' centroid.
func TestPlanCoreAnchorsOnPlan(t *testing.T) {
	plan := policy.LayoutPlan{
		Spine: []policy.SpineSegment{{From: domain.Cell{X: 10, Z: 20}, To: domain.Cell{X: 30, Z: 20}}},
		Rooms: []policy.PlannedRoom{{Role: policy.PlannedStorage, Interior: policy.Rectangle{X: 12, Z: 22, Width: 6, Height: 4}}},
	}
	if got, _ := planCore(observation.ColonyProjection{LayoutPlan: domain.Known(plan)}); got != (domain.Cell{X: 15, Z: 24}) {
		t.Fatalf("planCore = %v, want storeroom centre", got)
	}
	if got, ok := planCore(observation.ColonyProjection{}); ok {
		t.Fatalf("planCore without plan = %v, want none", got)
	}
}

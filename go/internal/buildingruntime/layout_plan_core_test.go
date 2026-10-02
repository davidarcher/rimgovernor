package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// TestPlanCoreAnchorsOnPlan: stockpiles and the cooking campfire anchor on
// the stored plan's storeroom, not the colonists' centroid (#1534).
func TestPlanCoreAnchorsOnPlan(t *testing.T) {
	centre := domain.Cell{X: 60, Z: 60}
	plan := policy.LayoutPlan{
		Spine: []policy.SpineSegment{{From: domain.Cell{X: 10, Z: 20}, To: domain.Cell{X: 30, Z: 20}}},
		Rooms: []policy.LayoutRoom{{Role: policy.ModuleStorage, Interior: policy.Rectangle{X: 12, Z: 22, Width: 6, Height: 4}}},
	}
	if got := planCore(observation.ColonyProjection{Center: centre, LayoutPlan: domain.Known(plan)}); got != (domain.Cell{X: 15, Z: 24}) {
		t.Fatalf("planCore = %v, want storeroom centre", got)
	}
	if got := planCore(observation.ColonyProjection{Center: centre}); got != centre {
		t.Fatalf("planCore without plan = %v, want %v", got, centre)
	}
}

package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestCraftingSpotSelection(t *testing.T) {
	r := &RoutineBuildingPlanner{goal: policy.EnsureBasicDefense, definition: craftingSpotDefinition}
	f := observation.ColonyProjection{Facts: policy.RoutineFacts{Colonists: domain.Known(int64(3))}}
	if n, id, reason := r.selection(f); n != 1 || id != "crafting-spot" || reason != "" {
		t.Fatal(n, id, reason)
	}
}

package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The roof grid reads the projection's planning window: listed cells are
// mirror rows, an unlisted cell is fogged, and the bounds and radius carry.
func TestColonyRoofGrid(t *testing.T) {
	listed := domain.Cell{X: 3, Z: 4}
	grid := colonyRoofGrid(observation.ColonyProjection{Bounds: policy.Bounds{Width: 100, Height: 80}, RoofSupport: 6, Cells: []policy.SiteCell{{Cell: listed}}})
	if _, ok := grid.Cell(listed); !ok {
		t.Fatal("listed cell missing")
	}
	if _, ok := grid.Cell(domain.Cell{X: 5, Z: 5}); ok {
		t.Fatal("unlisted cell read as known")
	}
	if grid.Bounds != (policy.Rectangle{Width: 100, Height: 80}) || grid.Radius != 6 {
		t.Fatalf("%+v", grid)
	}
}

func TestRecoveryBatchDecision(t *testing.T) {
	d := recoveryBatchDecision(3, policy.RecoveryBatch{Stage: policy.RecoveryBatchRemoval, Batch: []string{"a", "b"}, Held: []policy.RecoveryBatchHold{{ID: "c", Reason: policy.RemoteHoldRoofSupport}}})
	if d.Kind != "recovery_batch" || d.Verdict != "planned" || d.Reason != "removal" || d.Attrs["targets"] != 3 || len(d.Attrs["held"].([]map[string]any)) != 1 {
		t.Fatalf("%+v", d)
	}
	if none := recoveryBatchDecision(1, policy.RecoveryBatch{Stage: policy.RecoveryBatchNone}); none.Verdict != "held" {
		t.Fatalf("%+v", none)
	}
}

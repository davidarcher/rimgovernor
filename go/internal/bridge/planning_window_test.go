package bridge

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The window is the whole map, whatever the colonists and the plan cover.
func TestPlanningWindowRectIsTheWholeMap(t *testing.T) {
	bounds := policy.Bounds{Width: 100, Height: 60}
	want := policy.Rectangle{Width: 100, Height: 60}
	for _, plan := range []policy.Rectangle{{}, {X: 90, Z: 50, Width: 5, Height: 5}} {
		if got := PlanningWindowRect(domain.Cell{X: 50, Z: 30}, bounds, plan); got != want {
			t.Fatal(got)
		}
	}
}

func TestLayoutPlanExtentCoversRoomWalls(t *testing.T) {
	plan := policy.LayoutPlan{Rooms: []policy.LayoutRoom{{Role: policy.ModuleRole("bedroom"), Interior: policy.Rectangle{X: 10, Z: 10, Width: 3, Height: 3}}, {Role: policy.ModuleRole("bedroom"), Interior: policy.Rectangle{X: 128, Z: 131, Width: 9, Height: 7}}}}
	if got, ok := plan.Extent(); !ok || got != (policy.Rectangle{X: 9, Z: 9, Width: 129, Height: 130}) {
		t.Fatal(got, ok)
	}
	if _, ok := (policy.LayoutPlan{}).Extent(); ok {
		t.Fatal("empty plan has an extent")
	}
}

// TestPlanningWindowNeedsTheStream: the window is the frame grid's; a
// client without a snapshot stream never asks native for cells.
func TestPlanningWindowNeedsTheStream(t *testing.T) {
	client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*callResult, error) {
		t.Fatal("window read called native")
		return nil, nil
	}}, testBudget)
	if _, _, err := client.ReadPlanningWindow(context.Background(), pbIdentity(), policy.Rectangle{Width: 3, Height: 3}); !errors.Is(err, ErrUnavailable) {
		t.Fatal(err)
	}
}

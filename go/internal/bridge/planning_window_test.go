package bridge

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestPlanningWindowRectClipsToMap(t *testing.T) {
	bounds := policy.Bounds{Width: 100, Height: 60}
	if got := PlanningWindowRect(domain.Cell{X: 50, Z: 30}, bounds, policy.Rectangle{}); got != (policy.Rectangle{X: 28, Z: 8, Width: 45, Height: 45}) {
		t.Fatal(got)
	}
	if got := PlanningWindowRect(domain.Cell{X: 3, Z: 55}, bounds, policy.Rectangle{}); got != (policy.Rectangle{X: 0, Z: 33, Width: 26, Height: 27}) {
		t.Fatal(got)
	}
}

// TestPlanningWindowRectCoversPlanExtent is #1282: colonists at x 101-114
// put the window at x 85..129, and a planned Barracks walled from x 127
// to 137 must still be read, the union clipped to the map.
func TestPlanningWindowRectCoversPlanExtent(t *testing.T) {
	bounds := policy.Bounds{Width: 250, Height: 250}
	barracks := policy.Rectangle{X: 127, Z: 130, Width: 11, Height: 9}
	if got := PlanningWindowRect(domain.Cell{X: 107, Z: 120}, bounds, barracks); got != (policy.Rectangle{X: 85, Z: 98, Width: 53, Height: 45}) {
		t.Fatal(got)
	}
	if got := PlanningWindowRect(domain.Cell{X: 107, Z: 120}, policy.Bounds{Width: 135, Height: 250}, barracks); got != (policy.Rectangle{X: 85, Z: 98, Width: 50, Height: 45}) {
		t.Fatal("clip", got)
	}
	if got := PlanningWindowRect(domain.Cell{X: 107, Z: 120}, bounds, policy.Rectangle{X: 100, Z: 110, Width: 5, Height: 5}); got != (policy.Rectangle{X: 85, Z: 98, Width: 45, Height: 45}) {
		t.Fatal("inside", got)
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

package policy

import "testing"

func TestLayoutPlanCoreBoundsIsTheWallsBox(t *testing.T) {
	t.Parallel()
	if _, ok := (LayoutPlan{}).CoreBounds(); ok {
		t.Fatal("an empty plan has no bounds")
	}
	plan := LayoutPlan{Rooms: []LayoutRoom{{Role: ModuleStorage, Interior: Rectangle{X: 10, Z: 10, Width: 4, Height: 4}}, {Role: ModuleKitchen, Interior: Rectangle{X: 20, Z: 12, Width: 3, Height: 3}}}}
	if got, ok := plan.CoreBounds(); !ok || got != (Rectangle{X: 9, Z: 9, Width: 15, Height: 7}) {
		t.Fatalf("bounds %+v ok=%v", got, ok)
	}
}

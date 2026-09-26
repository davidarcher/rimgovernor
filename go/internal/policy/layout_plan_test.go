package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestLayoutPlanAnchorFallsBackToReserve(t *testing.T) {
	p := LayoutPlan{Rooms: []LayoutRoom{
		{Role: ModuleKitchen, Interior: Rectangle{X: 0, Z: 0, Width: 4, Height: 4}},
		{Role: ModuleKitchen, Interior: Rectangle{X: 10, Z: 0, Width: 6, Height: 4}},
		{Role: ModuleReserve, Interior: Rectangle{X: 20, Z: 0, Width: 2, Height: 2}},
	}}
	if c, ok := p.Anchor(ModuleKitchen, nil); !ok || c != (domain.Cell{X: 2, Z: 2}) {
		t.Fatal(c, ok)
	}
	if c, ok := p.Anchor(ModuleKitchen, func(r Rectangle) bool { return r.X != 0 }); !ok || c != (domain.Cell{X: 13, Z: 2}) {
		t.Fatal(c, ok)
	}
	if c, ok := p.Anchor(ModuleKitchen, func(r Rectangle) bool { return r.X == 20 }); !ok || c != (domain.Cell{X: 21, Z: 1}) {
		t.Fatal(c, ok)
	}
	if _, ok := p.Anchor(ModuleKitchen, func(Rectangle) bool { return false }); ok {
		t.Fatal("anchored with no free room")
	}
}
